using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ChaoChaoKvm.Services
{
    public sealed class RemotePeerSession : IDisposable
    {
        private readonly HttpPeerClient _client = new HttpPeerClient();
        private readonly KvmPeerManager _manager;
        private byte[] _latestFrame;
        private bool _disposed;
        private readonly int _activeFps;
        private readonly string _onlineAccessKey = "";
        private readonly bool _online;

        public RemotePeerSession(KvmPeerManager manager, string peerIp, string displayName, int activeFps)
        {
            _manager = manager;
            PeerIp = peerIp;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? peerIp : displayName;
            _activeFps = Math.Max(2, Math.Min(20, activeFps));
            _manager.TrustPeer(peerIp);
            RemoteInfo = _manager.GetPeerInfo(peerIp);
            _manager.RemoteFrameFromPeer += OnManagerFrame;
            _manager.RemoteInfoFromPeer += OnManagerInfo;
            _manager.StatusChanged += OnStatus;
            _client.RemoteFrame += OnFrame;
            _client.RemoteInfoChanged += OnInfo;
            _client.StatusChanged += OnStatus;
            _client.Log += delegate(string text) { if (Log != null) Log(text); };
            _client.SetFrameRate(2);
            if (!_manager.IsPeerConnected(peerIp)) _client.Start(peerIp, Protocol.DefaultPort, false, false);
        }

        public RemotePeerSession(string endpoint, string accessKey, string displayName, int activeFps)
        {
            _online = true;
            _onlineAccessKey = (accessKey ?? "").Trim();
            PeerIp = (endpoint ?? "").Trim().TrimEnd('/');
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? PeerIp : displayName;
            _activeFps = Math.Max(2, Math.Min(20, activeFps));
            _client.RemoteFrame += OnFrame;
            _client.RemoteInfoChanged += OnInfo;
            _client.StatusChanged += OnStatus;
            _client.Log += delegate(string text) { if (Log != null) Log(text); };
            _client.SetFrameRate(2);
            _client.StartEndpoint(PeerIp, _onlineAccessKey, false, false);
        }

        public string PeerIp { get; private set; }
        public string DisplayName { get; set; }
        public ScreenInfo RemoteInfo { get; private set; }
        public bool Connected { get { return !_disposed && (_client.Connected || (!_online && _manager != null && _manager.IsPeerConnected(PeerIp))); } }
        public byte[] LatestFrame { get { return Interlocked.CompareExchange(ref _latestFrame, null, null); } }

        public event Action<byte[]> FrameAvailable;
        public event Action<ScreenInfo> RemoteInfoChanged;
        public event Action StatusChanged;
        public event Action<string> Log;

        public void SetForeground(bool foreground)
        {
            if (_disposed) return;
            _client.SetFrameRate(foreground ? _activeFps : 2);
        }

        public void Retry()
        {
            if (_disposed) return;
            if (_online) _client.StartEndpoint(PeerIp, _onlineAccessKey, false, false);
            else _client.Start(PeerIp, Protocol.DefaultPort, false, false);
        }

        public void SendInput(Dictionary<string, object> input)
        {
            if (_disposed || input == null) return;
            if (_client.Connected && _client.SendInput(input)) return;
            if (!_online && _manager != null) _manager.SendInputToPeer(PeerIp, input);
        }

        private void OnManagerFrame(string peer, byte[] frame)
        {
            if (!string.Equals(peer, PeerIp, StringComparison.OrdinalIgnoreCase)) return;
            OnFrame(frame);
        }

        private void OnManagerInfo(string peer, ScreenInfo info)
        {
            if (!string.Equals(peer, PeerIp, StringComparison.OrdinalIgnoreCase)) return;
            OnInfo(info);
            OnStatus();
        }

        private void OnFrame(byte[] frame)
        {
            if (frame == null || frame.Length == 0) return;
            Interlocked.Exchange(ref _latestFrame, frame);
            if (FrameAvailable != null) FrameAvailable(frame);
        }

        private void OnInfo(ScreenInfo info)
        {
            RemoteInfo = info;
            if (info != null && !string.IsNullOrWhiteSpace(info.Machine) && (string.IsNullOrWhiteSpace(DisplayName) || DisplayName == PeerIp || DisplayName == "遠端電腦"))
                DisplayName = info.Machine;
            if (RemoteInfoChanged != null) RemoteInfoChanged(info);
        }

        private void OnStatus()
        {
            if (StatusChanged != null) StatusChanged();
        }

        public object Snapshot(bool active)
        {
            return new
            {
                ip = PeerIp,
                name = DisplayName,
                connected = Connected,
                active = active,
                width = RemoteInfo == null ? 0 : RemoteInfo.Width,
                height = RemoteInfo == null ? 0 : RemoteInfo.Height
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_manager != null)
            {
                _manager.RemoteFrameFromPeer -= OnManagerFrame;
                _manager.RemoteInfoFromPeer -= OnManagerInfo;
                _manager.StatusChanged -= OnStatus;
            }
            _client.Dispose();
        }
    }
}
