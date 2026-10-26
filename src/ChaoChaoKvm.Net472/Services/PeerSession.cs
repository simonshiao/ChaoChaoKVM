using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ChaoChaoKvm.Services
{
    public sealed class PeerSession : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly ScreenCaptureService _capture;
        private readonly InputInjectionService _input;
        private readonly object _sendLock = new object();
        private readonly Thread _receiveThread;
        private readonly Thread _captureThread;
        private volatile bool _running;
        private DateTime _lastFrameSent = DateTime.MinValue;

        public event Action<byte[]> RemoteFrame;
        public event Action<ScreenInfo> RemoteInfoChanged;
        public event Action Closed;
        public event Action<string> Log;

        public string PeerIp { get; private set; }
        public bool Outbound { get; private set; }
        public ScreenInfo RemoteInfo { get; private set; }
        public bool AllowRemoteControl { get; set; }
        public int Fps { get; set; }
        public int JpegQuality { get; set; }
        public bool Running { get { return _running; } }

        public PeerSession(TcpClient client, bool outbound, ScreenCaptureService capture, InputInjectionService input)
        {
            _client = client;
            _stream = client.GetStream();
            _capture = capture;
            _input = input;
            Outbound = outbound;
            IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
            PeerIp = ep == null ? "unknown" : ep.Address.ToString();
            Fps = 10;
            JpegQuality = 55;
            AllowRemoteControl = true;
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "ChaoKvmReceive" };
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ChaoKvmCapture" };
        }

        public void Start()
        {
            _client.NoDelay = true;
            _client.ReceiveTimeout = 30000;
            _client.SendTimeout = 30000;
            _running = true;
            SendInfo();
            _receiveThread.Start();
            _captureThread.Start();
            Emit("KVM 已建立：" + PeerIp + (Outbound ? " · 主動連線" : " · 對方連入"));
        }

        public void SendInput(Dictionary<string, object> input)
        {
            if (!_running) return;
            try { Send(Protocol.Input, JsonUtil.Utf8(input)); }
            catch { CloseInternal(); }
        }

        public void Ping()
        {
            if (_running) try { Send(Protocol.Ping, new byte[0]); } catch { CloseInternal(); }
        }

        private void SendInfo()
        {
            ScreenInfo info = _capture.GetInfo();
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["machine"] = info.Machine;
            data["left"] = info.Left;
            data["top"] = info.Top;
            data["width"] = info.Width;
            data["height"] = info.Height;
            data["version"] = Protocol.Version;
            Send(Protocol.Info, JsonUtil.Utf8(data));
        }

        private void CaptureLoop()
        {
            int infoCounter = 0;
            while (_running)
            {
                DateTime started = DateTime.UtcNow;
                try
                {
                    byte[] jpeg = _capture.CaptureJpeg(JpegQuality);
                    Send(Protocol.Frame, jpeg);
                    _lastFrameSent = DateTime.UtcNow;
                    if (++infoCounter >= Math.Max(20, Fps * 10)) { infoCounter = 0; SendInfo(); }
                }
                catch (Exception ex)
                {
                    Emit("畫面傳輸停止：" + ex.Message);
                    CloseInternal();
                    break;
                }

                int interval = Math.Max(33, 1000 / Math.Max(1, Fps));
                int spent = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (spent < interval) Thread.Sleep(interval - spent);
            }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_running)
                {
                    Packet packet = PacketCodec.Read(_stream);
                    if (packet.Kind == Protocol.Frame)
                    {
                        if (RemoteFrame != null) RemoteFrame(packet.Payload);
                    }
                    else if (packet.Kind == Protocol.Info)
                    {
                        Dictionary<string, object> data = JsonUtil.Parse(packet.Payload);
                        RemoteInfo = new ScreenInfo
                        {
                            Machine = JsonUtil.GetString(data, "machine", PeerIp),
                            Left = JsonUtil.GetInt(data, "left", 0),
                            Top = JsonUtil.GetInt(data, "top", 0),
                            Width = Math.Max(1, JsonUtil.GetInt(data, "width", 1920)),
                            Height = Math.Max(1, JsonUtil.GetInt(data, "height", 1080))
                        };
                        if (RemoteInfoChanged != null) RemoteInfoChanged(RemoteInfo);
                    }
                    else if (packet.Kind == Protocol.Input)
                    {
                        if (AllowRemoteControl) _input.Apply(packet.Payload, _capture.GetInfo());
                    }
                    else if (packet.Kind == Protocol.Ping)
                    {
                        // Ping is deliberately one-way in V1. Do not echo it,
                        // otherwise two symmetric peers would create a ping loop.
                    }
                    else if (packet.Kind == Protocol.Close)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running) Emit("KVM 連線中斷：" + ex.Message);
            }
            CloseInternal();
        }

        private void Send(byte kind, byte[] payload)
        {
            lock (_sendLock)
            {
                if (!_running && kind != Protocol.Info) return;
                PacketCodec.Write(_stream, kind, payload);
            }
        }

        private void CloseInternal()
        {
            if (!_running) return;
            _running = false;
            try { _client.Close(); } catch { }
            if (Closed != null) Closed();
        }

        private void Emit(string text) { if (Log != null) Log(text); }

        public void Dispose()
        {
            bool wasRunning = _running;
            _running = false;
            if (wasRunning) try { lock (_sendLock) { PacketCodec.Write(_stream, Protocol.Close, new byte[0]); } } catch { }
            try { _client.Close(); } catch { }
        }
    }
}
