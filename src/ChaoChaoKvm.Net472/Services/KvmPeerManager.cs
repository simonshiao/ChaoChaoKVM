using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace ChaoChaoKvm.Services
{
    // KVM 與傳檔統一使用 ChaoTransfer 已驗證的 HTTP 8001 核心。
    // 兩邊都開 HTTP Server；能 outbound 的那一邊負責主動 push/poll。
    // 若公司 inbound 被擋，家端的畫面 / Input / 檔案會留在 queue，等公司端主動拉回。
    public sealed class KvmPeerManager : IDisposable
    {
        private readonly object _sync = new object();
        private readonly ScreenCaptureService _capture = new ScreenCaptureService();
        private readonly InputInjectionService _input = new InputInjectionService();
        private readonly AppConfig _config;
        private readonly HttpPeerServer _server = new HttpPeerServer();
        private readonly HttpPeerClient _client = new HttpPeerClient();
        private Thread _captureThread;
        private Thread _pushThread;
        private readonly AutoResetEvent _pushReady = new AutoResetEvent(false);
        private OutgoingFrame _pendingFrame;
        private sealed class OutgoingFrame { public byte[] Jpeg; public ScreenInfo Info; public string Peer; }
        private volatile bool _running;
        private int _disposed;
        private string _activePeerIp;
        private string _lastPeerIp;
        private string _trustedPeerIp;
        private int _port;
        private readonly Dictionary<string, ScreenInfo> _remoteInfos = new Dictionary<string, ScreenInfo>(StringComparer.OrdinalIgnoreCase);

        public event Action<string> Log;
        public event Action StatusChanged;
        public event Action<byte[]> RemoteFrame;
        public event Action<ScreenInfo> RemoteInfoChanged;
        public event Action<string, byte[]> RemoteFrameFromPeer;
        public event Action<string, ScreenInfo> RemoteInfoFromPeer;
        public event Action<long, long, string> FileProgress;
        public event Action<object> JsonReceived;

        public KvmPeerManager(AppConfig config)
        {
            _config = config;
            _activePeerIp = config.ActivePeerIp ?? "";
            _lastPeerIp = config.LastPeerIp ?? "";
            _trustedPeerIp = config.LastPeerIp ?? "";
            _port = config.ListenPort > 0 ? config.ListenPort : Protocol.DefaultPort;
            _server.RequiredAccessKey = BuildFlavor.IsOnline ? (config.OnlineAccessKey ?? "") : "";

            _server.Log += Emit;
            _client.Log += Emit;
            _server.StatusChanged += RaiseStatus;
            _client.StatusChanged += RaiseStatus;
            _server.PeerSeen += OnPeerSeen;
            _client.RemoteFrame += ForwardClientRemoteFrame;
            _server.RemoteFrameFromPeer += ForwardServerRemoteFrame;
            _client.RemoteInfoChanged += ForwardClientRemoteInfo;
            _server.RemoteInfoFromPeer += ForwardServerRemoteInfo;
            _server.InputReceived += OnRemoteInput;
            _client.InputReceived += OnRemoteInput;
            _server.JsonReceived += ForwardJson;
            _client.JsonReceived += ForwardJson;
            _server.FileProgress += ForwardFileProgress;
            _client.FileProgress += ForwardFileProgress;
        }

        public bool Running { get { return _running; } }
        public bool Connected { get { return _client.Connected || _server.PeerAlive; } }
        public bool OutboundConnected { get { return _client.Connected; } }
        public bool InboundSeen { get { return _server.PeerAlive; } }
        public string ActivePeerIp { get { return _activePeerIp; } }
        public string LastPeerIp { get { return _lastPeerIp; } }
        public int Port { get { return _port; } }
        public ScreenInfo RemoteInfo { get; private set; }

        public void TrustPeer(string ip)
        {
            ip = (ip ?? "").Trim();
            if (ip.Length == 0 || NetworkUtils.IsLocalAddress(ip) || !NetworkUtils.IsPrivateOrLinkLocal(ip)) return;
            _server.TrustPeer(ip);
        }

        public bool IsPeerConnected(string ip)
        {
            ip = (ip ?? "").Trim();
            if (ip.Length == 0) return false;
            if (_server.IsPeerAlive(ip)) return true;
            return _client.Connected && string.Equals(_client.Host, ip, StringComparison.OrdinalIgnoreCase);
        }

        public ScreenInfo GetPeerInfo(string ip)
        {
            lock (_sync)
            {
                ScreenInfo info;
                return _remoteInfos.TryGetValue(ip ?? "", out info) ? info : null;
            }
        }

        public bool AllowRemoteControl
        {
            get { return _config.AllowRemoteControl; }
            set { _config.AllowRemoteControl = value; SaveConfig(); RaiseStatus(); }
        }

        public int Fps
        {
            get { return _config.Fps; }
            set
            {
                _config.Fps = Math.Max(2, Math.Min(20, value));
                _client.SetFrameRate(_config.Fps);
                SaveConfig();
            }
        }

        public int JpegQuality
        {
            get { return _config.JpegQuality; }
            set { _config.JpegQuality = Math.Max(25, Math.Min(85, value)); SaveConfig(); }
        }

        public string ReceiveDirectory { get { return _config.ReceiveDirectory; } }

        public void Start()
        {
            if (_running) return;
            _running = true;

            ConfigureTransfer(_config.ReceiveDirectory, _config.OverwriteFiles);
            _server.TrustedPeerIp = _trustedPeerIp;

            try
            {
                _server.Start(_port);
            }
            catch (Exception ex)
            {
                _running = false;
                SocketException socket = ex as SocketException;
                string cause = socket != null &&
                    socket.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "HTTP 8001 已被其他程式占用。請確認是否同時開了多份「吵吵 KVM」或舊 ChaoTransfer，關閉多餘的實例後再啟動。"
                    : "HTTP 8001 無法啟動，請確認網路服務與連接埠狀態。";
                throw new InvalidOperationException(cause + "\n" + ex.Message, ex);
            }

            // 升級舊版時可直接沿用 ChaoTransfer 已配對的 IP；之後本程式可獨立運作。
            ChaoTransferPeerState transfer = ChaoTransferDiscovery.TryGetState();
            string discovered = transfer == null ? "" : transfer.PeerIp;
            if (string.IsNullOrWhiteSpace(_activePeerIp)) _activePeerIp = !string.IsNullOrWhiteSpace(discovered) ? discovered : _lastPeerIp;
            if (!string.IsNullOrWhiteSpace(_activePeerIp) && !NetworkUtils.IsLocalAddress(_activePeerIp) && NetworkUtils.IsPrivateOrLinkLocal(_activePeerIp))
                StartClient(_activePeerIp);

            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ChaoKvmHttpCapture" };
            _captureThread.Start();
            _pushThread = new Thread(PushLoop) { IsBackground = true, Name = "ChaoKvmFramePush" };
            _pushThread.Start();
            Emit("自動啟動完成 · HTTP 8001 · KVM / 傳檔 / JSON 共用同一核心");
            RaiseStatus();
        }

        public void ConfigureTransfer(string receiveDirectory, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(receiveDirectory))
                receiveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ChaoChaoKvm");
            Directory.CreateDirectory(receiveDirectory);
            _config.ReceiveDirectory = receiveDirectory;
            _config.OverwriteFiles = overwrite;
            _server.ReceiveDirectory = receiveDirectory;
            _server.Overwrite = overwrite;
            _client.ReceiveDirectory = receiveDirectory;
            _client.Overwrite = overwrite;
            SaveConfig();
        }

        public void SetActivePeer(string ip)
        {
            ip = (ip ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                if (!NetworkUtils.IsPrivateOrLinkLocal(ip)) throw new InvalidOperationException("目前只允許 LAN / 169.254.x.x 私有網路位址。");
                if (NetworkUtils.IsLocalAddress(ip)) throw new InvalidOperationException("不能把本機 IP 設為遠端。");
            }

            _activePeerIp = ip;
            _config.ActivePeerIp = ip;
            _config.ManualActivePeer = !string.IsNullOrWhiteSpace(ip);
            if (!string.IsNullOrWhiteSpace(ip))
            {
                _lastPeerIp = ip;
                _trustedPeerIp = ip;
                _config.LastPeerIp = ip;
                _server.TrustedPeerIp = ip;
                StartClient(ip);
            }
            else
            {
                _client.Stop();
            }
            SaveConfig();
            RaiseStatus();
        }

        public void RetryNow()
        {
            if (!string.IsNullOrWhiteSpace(_activePeerIp)) StartClient(_activePeerIp);
            RaiseStatus();
        }

        public bool Ping(out string message)
        {
            if (_client.Connected) return _client.Ping(out message);
            if (_server.PeerAlive)
            {
                message = "對端正在主動透過 HTTP 8001 存取本機";
                return true;
            }
            message = "尚未與另一台建立 HTTP 8001 通訊";
            return false;
        }

        public void SendInput(Dictionary<string, object> input)
        {
            if (input == null) return;
            if (_client.Connected && _client.SendInput(input)) return;
            // 公司 inbound 被擋時：家端把 input 放 queue，公司端會主動 pull。
            _server.QueueInput(input);
        }

        public void SendInputToPeer(string peerIp, Dictionary<string, object> input)
        {
            peerIp = (peerIp ?? "").Trim();
            if (input == null || peerIp.Length == 0) return;
            if (_client.Connected && string.Equals(_client.Host, peerIp, StringComparison.OrdinalIgnoreCase) && _client.SendInput(input)) return;
            _server.QueueInput(peerIp, input);
        }

        public string SendJson(object payload)
        {
            if (_client.Connected)
            {
                try { return _client.SendJson(payload); } catch { }
            }
            return _server.QueueJson(_activePeerIp, payload);
        }

        public string SendPaths(IEnumerable<string> paths)
        {
            List<string> list = new List<string>();
            foreach (string p in paths) if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p))) list.Add(p);
            if (list.Count == 0) throw new InvalidOperationException("沒有可傳送的檔案或資料夾");

            string id = Guid.NewGuid().ToString("N");
            if (_client.Connected)
            {
                _client.SendPaths(list);
                return id;
            }

            // 沒有 outbound 時沿用 ChaoTransfer 的 pull queue：等對方主動 GET 回去。
            QueuePathsForPull(list);
            return id;
        }

        private void QueuePathsForPull(IEnumerable<string> paths)
        {
            foreach (string raw in paths)
            {
                if (File.Exists(raw))
                {
                    _server.QueueFile(_activePeerIp, raw, Path.GetFileName(raw));
                }
                else if (Directory.Exists(raw))
                {
                    DirectoryInfo root = new DirectoryInfo(raw);
                    foreach (string file in Directory.GetFiles(raw, "*", SearchOption.AllDirectories))
                    {
                        string suffix = file.Substring(root.FullName.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                        _server.QueueFile(_activePeerIp, file, root.Name + Path.DirectorySeparatorChar + suffix);
                    }
                }
            }
            Emit("已加入回拉 Queue，等另一台主動 HTTP 取回");
        }

        public Dictionary<string, object> Snapshot()
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["app"] = Protocol.AppName;
            data["version"] = Protocol.Version;
            data["protocol"] = Protocol.ProtocolName;
            data["transport"] = "http";
            data["running"] = _running;
            data["connected"] = Connected;
            data["outbound_connected"] = _client.Connected;
            data["inbound_seen"] = _server.PeerAlive;
            data["active_peer_ip"] = _activePeerIp;
            data["last_peer_ip"] = _lastPeerIp;
            data["trusted_peer_ip"] = _trustedPeerIp;
            data["port"] = _port;
            data["allow_remote_control"] = AllowRemoteControl;
            data["receive_directory"] = _config.ReceiveDirectory;
            if (RemoteInfo != null)
            {
                data["remote_machine"] = RemoteInfo.Machine;
                data["remote_width"] = RemoteInfo.Width;
                data["remote_height"] = RemoteInfo.Height;
            }
            return data;
        }

        private void StartClient(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || NetworkUtils.IsLocalAddress(ip) || !NetworkUtils.IsPrivateOrLinkLocal(ip)) return;
            lock (_sync)
            {
                _activePeerIp = ip;
                _client.ReceiveDirectory = _config.ReceiveDirectory;
                _client.Overwrite = _config.OverwriteFiles;
                _client.SetFrameRate(_config.Fps);
                _client.Start(ip, _port);
            }
        }

        private void OnPeerSeen(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || NetworkUtils.IsLocalAddress(ip) || !NetworkUtils.IsPrivateOrLinkLocal(ip)) return;
            bool first = string.IsNullOrWhiteSpace(_trustedPeerIp);
            if (!first && !string.Equals(_trustedPeerIp, ip, StringComparison.OrdinalIgnoreCase)) return;
            if (!first && string.Equals(_lastPeerIp, ip, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_activePeerIp)) return;

            _lastPeerIp = ip;
            _trustedPeerIp = ip;
            _server.TrustedPeerIp = ip;
            _config.LastPeerIp = ip;
            if (string.IsNullOrWhiteSpace(_activePeerIp))
            {
                _activePeerIp = ip;
                _config.ActivePeerIp = ip;
                StartClient(ip); // 若公司 inbound 被擋，這條會失敗，但不影響 server queue/pull。
            }
            SaveConfig();
            RaiseStatus();
        }

        private void CaptureLoop()
        {
            try
            {
            while (_running)
            {
                DateTime start = DateTime.UtcNow;
                try
                {
                    ScreenInfo info = _capture.GetInfo();
                    byte[] jpeg = _capture.CaptureJpeg(_config.JpegQuality);
                    _server.SetLocalFrame(jpeg, info);
                    if (_client.Connected) { Interlocked.Exchange(ref _pendingFrame, new OutgoingFrame { Jpeg = jpeg, Info = info, Peer = _activePeerIp }); _pushReady.Set(); }
                }
                catch (Exception ex) { Emit("畫面擷取：" + ex.Message); }

                int frameMs = Math.Max(45, 1000 / Math.Max(2, _config.Fps));
                int elapsed = (int)(DateTime.UtcNow - start).TotalMilliseconds;
                Thread.Sleep(Math.Max(5, frameMs - elapsed));
            }
            }
            finally { _capture.Dispose(); }
        }

        private void OnRemoteInput(Dictionary<string, object> input)
        {
            if (!_config.AllowRemoteControl) return;
            try { _input.Apply(JsonUtil.Utf8(input), _capture.GetInfo()); }
            catch (Exception ex) { Emit("遠端輸入：" + ex.Message); }
        }

        private void ForwardRemoteFrame(byte[] data)
        {
            if (RemoteFrame != null) RemoteFrame(data);
        }

        private void ForwardClientRemoteFrame(byte[] data)
        {
            ForwardRemoteFrame(data);
            string peer = _activePeerIp;
            if (!string.IsNullOrWhiteSpace(peer) && RemoteFrameFromPeer != null) RemoteFrameFromPeer(peer, data);
        }

        private void ForwardServerRemoteFrame(string peer, byte[] data)
        {
            if (string.Equals(peer, _activePeerIp, StringComparison.OrdinalIgnoreCase)) ForwardRemoteFrame(data);
            if (RemoteFrameFromPeer != null) RemoteFrameFromPeer(peer, data);
        }

        private void ForwardRemoteInfo(ScreenInfo info)
        {
            ScreenInfo previous = RemoteInfo;
            if (previous != null && info != null && previous.Width == info.Width && previous.Height == info.Height && previous.Left == info.Left && previous.Top == info.Top && previous.Machine == info.Machine) return;
            RemoteInfo = info;
            if (RemoteInfoChanged != null) RemoteInfoChanged(info);
            RaiseStatus();
        }

        private void ForwardClientRemoteInfo(ScreenInfo info)
        {
            ForwardRemoteInfo(info);
            string peer = _activePeerIp;
            if (string.IsNullOrWhiteSpace(peer)) return;
            lock (_sync) _remoteInfos[peer] = info;
            if (RemoteInfoFromPeer != null) RemoteInfoFromPeer(peer, info);
        }

        private void ForwardServerRemoteInfo(string peer, ScreenInfo info)
        {
            if (string.IsNullOrWhiteSpace(peer)) return;
            lock (_sync) _remoteInfos[peer] = info;
            if (string.Equals(peer, _activePeerIp, StringComparison.OrdinalIgnoreCase)) ForwardRemoteInfo(info);
            if (RemoteInfoFromPeer != null) RemoteInfoFromPeer(peer, info);
        }

        private void ForwardFileProgress(long current, long total, string name)
        {
            if (FileProgress != null) FileProgress(current, total, name);
        }

        private void ForwardJson(object payload)
        {
            if (JsonReceived != null) JsonReceived(payload);
        }

        private void SaveConfig()
        {
            try { _config.Save(); } catch { }
        }

        private void Emit(string text) { if (Log != null) Log(text); }
        private void RaiseStatus() { if (StatusChanged != null) StatusChanged(); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _running = false;
            Interlocked.Exchange(ref _pendingFrame, null);
            _pushReady.Set();
            // Stop accepting requests even if the outbound client fails to stop.
            try { _server.Dispose(); }
            finally { _client.Dispose(); }
            // Finish background capture/push before ending the app; bounded to
            // avoid blocking shutdown indefinitely on a broken network.
            if (_captureThread != null && _captureThread.IsAlive) _captureThread.Join(1200);
            if (_pushThread != null && _pushThread.IsAlive) _pushThread.Join(1200);
        }

        private void PushLoop()
        {
            while (_running)
            {
                _pushReady.WaitOne(250);
                OutgoingFrame frame = Interlocked.Exchange(ref _pendingFrame, null);
                if (_running && frame != null && _client.Connected && string.Equals(frame.Peer, _activePeerIp, StringComparison.OrdinalIgnoreCase)) _client.PushFrame(frame.Jpeg, frame.Info);
            }
        }
    }
}
