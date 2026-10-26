using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ChaoChaoKvm.Services
{
    internal sealed class HttpQueuedItem
    {
        public string Id;
        public string Type;
        public string TargetPeer;
        public string FilePath;
        public string RelativePath;
        public object JsonPayload;
        public long Size;
    }

    public sealed class HttpPeerServer : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Queue<string> _queue = new Queue<string>();
        private readonly Dictionary<string, HttpQueuedItem> _items = new Dictionary<string, HttpQueuedItem>();
        private readonly Dictionary<string, LinkedList<Dictionary<string, object>>> _inputQueues = new Dictionary<string, LinkedList<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _peerSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _trustedPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private DateTime _lastPeerSeen = DateTime.MinValue;
        private string _lastPeerIp = "";
        private byte[] _localFrame;
        private ScreenInfo _localInfo;
        private long _localFrameSeq;

        public string ReceiveDirectory { get; set; }
        public bool Overwrite { get; set; }
        public string TrustedPeerIp { get; set; }
        public string RequiredAccessKey { get; set; }
        public int Port { get; private set; }

        public event Action<string> Log;
        public event Action StatusChanged;
        public event Action<string> PeerSeen;
        public event Action FrameRequested;
        public event Action<byte[]> RemoteFrame;
        public event Action<ScreenInfo> RemoteInfoChanged;
        public event Action<string, byte[]> RemoteFrameFromPeer;
        public event Action<string, ScreenInfo> RemoteInfoFromPeer;
        public event Action<Dictionary<string, object>> InputReceived;
        public event Action<object> JsonReceived;
        public event Action<long, long, string> FileProgress;

        public HttpPeerServer()
        {
            ReceiveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ChaoChaoKvm");
        }

        public bool Running { get { return _running; } }
        public bool PeerAlive { get { return DateTime.Now - _lastPeerSeen < TimeSpan.FromSeconds(6); } }
        public string LastPeerIp { get { return _lastPeerIp; } }

        public bool IsPeerAlive(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            lock (_sync)
            {
                DateTime seen;
                return _peerSeen.TryGetValue(ip, out seen) && DateTime.Now - seen < TimeSpan.FromSeconds(6);
            }
        }

        public void TrustPeer(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || !NetworkUtils.IsPrivateOrLinkLocal(ip)) return;
            lock (_sync) _trustedPeers.Add(ip.Trim());
        }

        public void Start(int port)
        {
            Start(port, IPAddress.Any);
        }

        public void Start(int port, IPAddress bindAddress)
        {
            if (_running) return;
            Port = port;
            _listener = new TcpListener(bindAddress, port);
            _listener.Start(32);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ChaoKvmHttpServer" };
            _acceptThread.Start();
            Emit("HTTP 核心已啟動：0.0.0.0:" + port);
        }

        public void SetLocalFrame(byte[] jpeg, ScreenInfo info)
        {
            if (jpeg == null || jpeg.Length == 0 || info == null) return;
            lock (_sync)
            {
                _localFrame = jpeg;
                _localInfo = info;
                _localFrameSeq++;
            }
        }

        public void QueueInput(Dictionary<string, object> input)
        {
            QueueInput(TrustedPeerIp, input);
        }

        public void QueueInput(string peerIp, Dictionary<string, object> input)
        {
            if (input == null) return;
            peerIp = (peerIp ?? "").Trim();
            lock (_sync)
            {
                LinkedList<Dictionary<string, object>> queue = GetInputQueueLocked(peerIp);
                // Only coalesce adjacent moves; never clear key/button releases when the mouse moves.
                string kind = JsonUtil.GetString(input, "kind", "");
                if (kind == "move" && queue.Last != null && JsonUtil.GetString(queue.Last.Value, "kind", "") == "move") queue.RemoveLast();
                queue.AddLast(input);
                // Bounded queue, fail explicitly instead of silently dropping a release event.
                if (queue.Count > 4096) { queue.RemoveLast(); throw new InvalidOperationException("遠端輸入佇列已滿，請停止控制並重新連線。"); }
            }
        }

        private LinkedList<Dictionary<string, object>> GetInputQueueLocked(string peerIp)
        {
            string key = string.IsNullOrWhiteSpace(peerIp) ? "*" : peerIp;
            LinkedList<Dictionary<string, object>> queue;
            if (!_inputQueues.TryGetValue(key, out queue)) { queue = new LinkedList<Dictionary<string, object>>(); _inputQueues[key] = queue; }
            return queue;
        }

        public string QueueJson(object payload)
        {
            return QueueJson(TrustedPeerIp, payload);
        }

        public string QueueJson(string peerIp, object payload)
        {
            HttpQueuedItem item = new HttpQueuedItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "json",
                TargetPeer = (peerIp ?? "").Trim(),
                JsonPayload = payload,
                Size = Encoding.UTF8.GetByteCount(JsonUtil.Serialize(payload))
            };
            Enqueue(item);
            return item.Id;
        }

        public string QueueFile(string filePath, string relativePath)
        {
            return QueueFile(TrustedPeerIp, filePath, relativePath);
        }

        public string QueueFile(string peerIp, string filePath, string relativePath)
        {
            FileInfo info = new FileInfo(filePath);
            HttpQueuedItem item = new HttpQueuedItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "file",
                TargetPeer = (peerIp ?? "").Trim(),
                FilePath = filePath,
                RelativePath = relativePath,
                Size = info.Length
            };
            Enqueue(item);
            return item.Id;
        }

        private void Enqueue(HttpQueuedItem item)
        {
            lock (_sync)
            {
                _items[item.Id] = item;
                _queue.Enqueue(item.Id);
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
                catch (SocketException) { if (!_running) break; }
                catch (Exception ex) { if (_running) Emit("HTTP Server：" + ex.Message); }
            }
        }

        private void HandleClient(TcpClient client)
        {
            string remote = "";
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                client.NoDelay = true;
                IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
                remote = ep == null ? "" : ep.Address.ToString();

                if (!IsAllowedRemote(remote))
                {
                    using (client)
                    using (NetworkStream denied = client.GetStream())
                        HttpWire.WriteJson(denied, 403, new Dictionary<string, object> { { "ok", false }, { "error", "untrusted_peer" } });
                    Emit("拒絕未配對 HTTP 對端：" + remote);
                    return;
                }

                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    HttpRequestData request = HttpWire.ReadRequest(stream);
                    if (!IsAuthorized(request))
                    {
                        HttpWire.WriteJson(stream, 401, new Dictionary<string, object> { { "ok", false }, { "error", "unauthorized" } });
                        Emit("拒絕未授權 Online KVM 請求：" + remote);
                        return;
                    }
                    Route(request, stream, remote);
                }
            }
            catch (Exception ex)
            {
                if (_running) Emit("HTTP 請求失敗 " + remote + "：" + ex.Message);
            }
        }

        private bool IsAllowedRemote(string remote)
        {
            IPAddress address;
            if (!IPAddress.TryParse(remote, out address)) return false;
            if (IPAddress.IsLoopback(address)) return true;
            if (!NetworkUtils.IsPrivateOrLinkLocal(remote)) return false;
            lock (_sync)
            {
                if (_trustedPeers.Contains(remote)) return true;
                if (string.IsNullOrWhiteSpace(TrustedPeerIp)) return true;
                return string.Equals(TrustedPeerIp, remote, StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool IsAuthorized(HttpRequestData request)
        {
            string required = (RequiredAccessKey ?? "").Trim();
            if (required.Length == 0) return true;
            string supplied;
            if (request == null || request.Headers == null || !request.Headers.TryGetValue("X-Chao-Access-Key", out supplied)) return false;
            return FixedTimeEquals(required, supplied ?? "");
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            byte[] a = Encoding.UTF8.GetBytes(left ?? "");
            byte[] b = Encoding.UTF8.GetBytes(right ?? "");
            int diff = a.Length ^ b.Length;
            int count = Math.Max(a.Length, b.Length);
            for (int i = 0; i < count; i++)
            {
                byte av = i < a.Length ? a[i] : (byte)0;
                byte bv = i < b.Length ? b[i] : (byte)0;
                diff |= av ^ bv;
            }
            return diff == 0;
        }

        private void Route(HttpRequestData request, NetworkStream stream, string remote)
        {
            string pathOnly = request.Path.Split('?')[0];

            if (request.Method == "GET" && pathOnly == "/api/v1/health")
            {
                TouchPeer(remote);
                HttpWire.WriteJson(stream, 200, new Dictionary<string, object>
                {
                    { "ok", true }, { "app", Protocol.AppName }, { "version", Protocol.Version },
                    { "protocol", Protocol.ProtocolName }, { "transport", "http" }
                });
                return;
            }

            if (request.Method == "POST" && pathOnly == "/api/v1/heartbeat")
            {
                TouchPeer(remote);
                if (request.ContentLength > 0) HttpWire.ReadBody(request, Protocol.MaxJsonBytes);
                HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "time", DateTimeOffset.Now.ToString("o") } });
                return;
            }

            if (request.Method == "PUT" && pathOnly == "/api/v1/kvm/push/frame")
            {
                TouchPeer(remote);
                byte[] jpeg = HttpWire.ReadBody(request, Protocol.MaxFrameBytes);
                ScreenInfo info = ScreenInfoFromHeaders(request.Headers);
                if (RemoteInfoChanged != null && info != null) RemoteInfoChanged(info);
                if (RemoteFrame != null) RemoteFrame(jpeg);
                if (RemoteInfoFromPeer != null && info != null) RemoteInfoFromPeer(remote, info);
                if (RemoteFrameFromPeer != null) RemoteFrameFromPeer(remote, jpeg);
                HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "bytes", jpeg.Length } });
                return;
            }

            if (request.Method == "GET" && pathOnly == "/api/v1/kvm/pull/frame")
            {
                TouchPeer(remote);
                if (FrameRequested != null) FrameRequested();
                long after = ParseQueryLong(request.Path, "after", -1);
                byte[] frame;
                ScreenInfo info;
                long seq;
                lock (_sync)
                {
                    frame = _localFrame;
                    info = _localInfo;
                    seq = _localFrameSeq;
                }
                if (frame == null || frame.Length == 0 || seq <= after)
                {
                    HttpWire.WriteEmpty(stream, 204, new Dictionary<string, string> { { "X-Chao-Frame-Seq", seq.ToString() } });
                    return;
                }
                Dictionary<string, string> headers = ScreenHeaders(info);
                headers["X-Chao-Frame-Seq"] = seq.ToString();
                HttpWire.WriteBytes(stream, 200, "image/jpeg", frame, headers);
                return;
            }

            if (request.Method == "POST" && pathOnly == "/api/v1/kvm/push/input")
            {
                TouchPeer(remote);
                Dictionary<string, object> input = JsonUtil.Parse(HttpWire.ReadBody(request, Protocol.MaxJsonBytes));
                if (InputReceived != null) InputReceived(input);
                HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true } });
                return;
            }

            if (request.Method == "GET" && pathOnly == "/api/v1/kvm/pull/input")
            {
                TouchPeer(remote);
                LinkedList<Dictionary<string, object>> queue;
                lock (_sync) queue = GetInputQueueLocked(remote);
                if (ParseQueryLong(request.Path, "batch", 0) == 1)
                {
                    List<Dictionary<string, object>> batch = new List<Dictionary<string, object>>();
                    lock (_sync) while (queue.Count > 0 && batch.Count < 64) { batch.Add(queue.First.Value); queue.RemoveFirst(); }
                    HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "available", batch.Count > 0 }, { "payloads", batch } });
                    return;
                }
                Dictionary<string, object> input = null;
                lock (_sync) if (queue.Count > 0) { input = queue.First.Value; queue.RemoveFirst(); }
                if (input == null)
                {
                    HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "available", false } });
                    return;
                }
                HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "available", true }, { "payload", input } });
                return;
            }

            // 以下沿用 ChaoTransfer 已驗證的檔案 / JSON API。
            if (request.Method == "PUT" && pathOnly == "/api/v1/push/file")
            {
                TouchPeer(remote);
                ReceiveFile(request, stream, remote);
                return;
            }

            if (request.Method == "POST" && pathOnly == "/api/v1/push/json")
            {
                TouchPeer(remote);
                object payload = JsonUtil.Parse(HttpWire.ReadBody(request, Protocol.MaxJsonBytes));
                if (JsonReceived != null) JsonReceived(payload);
                HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true } });
                return;
            }

            if (request.Method == "GET" && pathOnly == "/api/v1/pull/next")
            {
                TouchPeer(remote);
                HttpQueuedItem item = PeekNext(remote);
                if (item == null)
                {
                    HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "available", false } });
                    return;
                }
                Dictionary<string, object> response = new Dictionary<string, object>();
                response["ok"] = true;
                response["available"] = true;
                response["id"] = item.Id;
                response["type"] = item.Type;
                response["size"] = item.Size;
                response["path_b64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(item.RelativePath ?? ""));
                if (item.Type == "json") response["json"] = item.JsonPayload;
                HttpWire.WriteJson(stream, 200, response);
                return;
            }

            if (request.Method == "GET" && pathOnly.StartsWith("/api/v1/pull/file/", StringComparison.Ordinal))
            {
                TouchPeer(remote);
                string id = pathOnly.Substring("/api/v1/pull/file/".Length);
                HttpQueuedItem item = GetItem(id, remote);
                if (item == null || item.Type != "file" || !File.Exists(item.FilePath))
                {
                    HttpWire.WriteJson(stream, 404, new Dictionary<string, object> { { "ok", false }, { "error", "file_not_found" } });
                    return;
                }
                Dictionary<string, string> headers = new Dictionary<string, string>();
                headers["X-Chao-Path-B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(item.RelativePath ?? Path.GetFileName(item.FilePath)));
                HttpWire.WriteFile(stream, item.FilePath, headers, delegate(long current, long total)
                {
                    if (FileProgress != null) FileProgress(current, total, item.RelativePath);
                });
                return;
            }

            if (request.Method == "POST" && pathOnly.StartsWith("/api/v1/pull/ack/", StringComparison.Ordinal))
            {
                TouchPeer(remote);
                string id = pathOnly.Substring("/api/v1/pull/ack/".Length);
                Ack(id, remote);
                if (request.ContentLength > 0) HttpWire.ReadBody(request, 1024);
                HttpWire.WriteJson(stream, 200, new Dictionary<string, object> { { "ok", true } });
                return;
            }

            HttpWire.WriteJson(stream, 404, new Dictionary<string, object> { { "ok", false }, { "error", "not_found" } });
        }

        private void ReceiveFile(HttpRequestData request, NetworkStream stream, string remote)
        {
            string encoded;
            if (!request.Headers.TryGetValue("X-Chao-Path-B64", out encoded) || string.IsNullOrWhiteSpace(encoded))
            {
                HttpWire.WriteJson(stream, 400, new Dictionary<string, object> { { "ok", false }, { "error", "missing_path" } });
                return;
            }
            string relative = SanitizeRelativePath(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            Directory.CreateDirectory(ReceiveDirectory);
            string target = Path.Combine(ReceiveDirectory, relative);
            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!Overwrite) target = UniquePath(target);
            string part = target + ".chao-part";
            HttpWire.CopyBodyToFile(request, part, delegate(long current, long total)
            {
                if (FileProgress != null) FileProgress(current, total, relative);
            });
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            Emit("接收完成：" + target + " · " + NetworkUtils.HumanSize(request.ContentLength));
            HttpWire.WriteJson(stream, 201, new Dictionary<string, object> { { "ok", true }, { "path", target } });
        }

        private void TouchPeer(string remote)
        {
            if (string.IsNullOrWhiteSpace(remote)) return;
            IPAddress address;
            if (IPAddress.TryParse(remote, out address) && IPAddress.IsLoopback(address)) return;
            bool wasAlive = PeerAlive;
            DateTime now = DateTime.Now;
            _lastPeerIp = remote;
            _lastPeerSeen = now;
            lock (_sync) _peerSeen[remote] = now;
            if (PeerSeen != null) PeerSeen(remote);
            if (!wasAlive && StatusChanged != null) StatusChanged();
        }

        private HttpQueuedItem PeekNext(string remote)
        {
            lock (_sync)
            {
                int count = _queue.Count;
                for (int i = 0; i < count; i++)
                {
                    string id = _queue.Dequeue();
                    HttpQueuedItem item;
                    if (!_items.TryGetValue(id, out item)) continue;
                    _queue.Enqueue(id);
                    if (string.IsNullOrWhiteSpace(item.TargetPeer) || string.Equals(item.TargetPeer, remote, StringComparison.OrdinalIgnoreCase)) return item;
                }
                return null;
            }
        }

        private HttpQueuedItem GetItem(string id, string remote)
        {
            lock (_sync)
            {
                HttpQueuedItem item;
                if (!_items.TryGetValue(id, out item)) return null;
                if (!string.IsNullOrWhiteSpace(item.TargetPeer) && !string.Equals(item.TargetPeer, remote, StringComparison.OrdinalIgnoreCase)) return null;
                return item;
            }
        }

        private void Ack(string id, string remote)
        {
            lock (_sync)
            {
                HttpQueuedItem item;
                if (!_items.TryGetValue(id, out item)) return;
                if (!string.IsNullOrWhiteSpace(item.TargetPeer) && !string.Equals(item.TargetPeer, remote, StringComparison.OrdinalIgnoreCase)) return;
                _items.Remove(id);
                if (_queue.Count > 0 && _queue.Peek() == id) _queue.Dequeue();
            }
        }

        private static Dictionary<string, string> ScreenHeaders(ScreenInfo info)
        {
            Dictionary<string, string> h = new Dictionary<string, string>();
            if (info == null) return h;
            h["X-Chao-Screen-Left"] = info.Left.ToString();
            h["X-Chao-Screen-Top"] = info.Top.ToString();
            h["X-Chao-Screen-Width"] = info.Width.ToString();
            h["X-Chao-Screen-Height"] = info.Height.ToString();
            h["X-Chao-Machine-B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(info.Machine ?? ""));
            return h;
        }

        private static ScreenInfo ScreenInfoFromHeaders(Dictionary<string, string> headers)
        {
            int left = HeaderInt(headers, "X-Chao-Screen-Left", 0);
            int top = HeaderInt(headers, "X-Chao-Screen-Top", 0);
            int width = HeaderInt(headers, "X-Chao-Screen-Width", 1920);
            int height = HeaderInt(headers, "X-Chao-Screen-Height", 1080);
            string machine = "REMOTE";
            string encoded;
            try { if (headers.TryGetValue("X-Chao-Machine-B64", out encoded)) machine = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); } catch { }
            return new ScreenInfo { Left = left, Top = top, Width = Math.Max(1, width), Height = Math.Max(1, height), Machine = machine };
        }

        private static int HeaderInt(Dictionary<string, string> headers, string name, int fallback)
        {
            string raw;
            int value;
            return headers.TryGetValue(name, out raw) && int.TryParse(raw, out value) ? value : fallback;
        }

        private static long ParseQueryLong(string path, string name, long fallback)
        {
            int q = path.IndexOf('?');
            if (q < 0 || q >= path.Length - 1) return fallback;
            string[] pairs = path.Substring(q + 1).Split('&');
            foreach (string pair in pairs)
            {
                string[] kv = pair.Split(new[] { '=' }, 2);
                long value;
                if (kv.Length == 2 && string.Equals(kv[0], name, StringComparison.OrdinalIgnoreCase) && long.TryParse(kv[1], out value)) return value;
            }
            return fallback;
        }

        private static string SanitizeRelativePath(string raw)
        {
            string value = (raw ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string[] parts = value.Split(Path.DirectorySeparatorChar);
            List<string> clean = new List<string>();
            foreach (string part in parts) if (!string.IsNullOrEmpty(part) && part != "." && part != "..") clean.Add(part);
            if (clean.Count == 0) return "download.bin";
            return Path.Combine(clean.ToArray());
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
            throw new IOException("無法建立不重複檔名");
        }

        private void Emit(string text) { if (Log != null) Log(text); }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }
    }
}
