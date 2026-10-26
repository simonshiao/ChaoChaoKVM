using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ChaoChaoKvm.Services
{
    public sealed class HttpPeerClient : IDisposable
    {
        private readonly object _lifecycle = new object();
        private CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, HttpWebRequest> _requests = new ConcurrentDictionary<int, HttpWebRequest>();
        [ThreadStatic] private static CancellationToken _workerToken;
        [ThreadStatic] private static CancellationToken _requestToken;
        private volatile bool _running;
        private DateTime _lastOk = DateTime.MinValue;
        [ThreadStatic] private static long _lastRemoteFrameSeq;
        private int _pullFrameIntervalMs = 100;
        private string _baseUrl = "";
        private string _accessKey = "";

        public string Host { get; private set; }
        public int Port { get; private set; }
        public string BaseUrl { get { return _baseUrl; } }
        public string ReceiveDirectory { get; set; }
        public bool Overwrite { get; set; }

        public event Action<string> Log;
        public event Action StatusChanged;
        public event Action<byte[]> RemoteFrame;
        public event Action<ScreenInfo> RemoteInfoChanged;
        public event Action<Dictionary<string, object>> InputReceived;
        public event Action<object> JsonReceived;
        public event Action<long, long, string> FileProgress;

        public HttpPeerClient()
        {
            ReceiveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ChaoChaoKvm");
        }

        public bool Running { get { return _running; } }
        public bool Connected { get { return DateTime.Now - _lastOk < TimeSpan.FromSeconds(5); } }

        public void SetFrameRate(int fps)
        {
            _pullFrameIntervalMs = Math.Max(45, 1000 / Math.Max(2, Math.Min(20, fps)));
        }

        public void Start(string host, int port)
        {
            Start(host, port, true, true);
        }

        public void Start(string host, int port, bool pullInput, bool pullTransfer)
        {
            StartCore("http://" + host + ":" + port, host, port, "", pullInput, pullTransfer);
        }

        public void StartEndpoint(string endpoint, string accessKey)
        {
            StartEndpoint(endpoint, accessKey, false, false);
        }

        public void StartEndpoint(string endpoint, string accessKey, bool pullInput, bool pullTransfer)
        {
            Uri uri;
            if (!Uri.TryCreate((endpoint ?? "").Trim(), UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && !string.Equals(uri.Scheme, "tcp", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Online KVM 位址必須是 tcp://、http:// 或 https:// URL。");
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new InvalidOperationException("Online KVM 位址不能包含 query 或 fragment。");
            string root = string.Equals(uri.Scheme, "tcp", StringComparison.OrdinalIgnoreCase)
                ? "http://" + uri.Host + ":" + uri.Port
                : uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            StartCore(root, uri.Host, uri.Port, accessKey, pullInput, pullTransfer);
        }

        private void StartCore(string baseUrl, string host, int port, string accessKey, bool pullInput, bool pullTransfer)
        {
            lock (_lifecycle)
            {
                Stop();
                Host = host; Port = port;
                _baseUrl = (baseUrl ?? "").TrimEnd('/');
                _accessKey = (accessKey ?? "").Trim();
                _cancellation = new CancellationTokenSource();
                _running = true;
                CancellationToken token = _cancellation.Token;
                StartWorker("Frame", token, FrameLoop);
                if (pullInput) StartWorker("Input", token, InputLoop);
                if (pullTransfer) StartWorker("Transfer", token, TransferLoop);
            }
        }

        public void Stop()
        {
            lock (_lifecycle)
            {
                _running = false;
                _cancellation.Cancel();
                foreach (HttpWebRequest request in _requests.Values) { try { request.Abort(); } catch { } }
                _requests.Clear();
                _lastOk = DateTime.MinValue;
                if (StatusChanged != null) StatusChanged();
            }
        }

        private void StartWorker(string name, CancellationToken token, Action<CancellationToken> run)
        {
            new Thread(delegate() { _workerToken = token; try { run(token); } finally { HttpWebRequest ignored; _requests.TryRemove(Thread.CurrentThread.ManagedThreadId, out ignored); _workerToken = default(CancellationToken); } })
                { IsBackground = true, Name = "ChaoKvmHttp" + name }.Start();
        }

        private void FrameLoop(CancellationToken token)
        {
            _lastRemoteFrameSeq = -1;
            bool oldState = false;
            while (!token.IsCancellationRequested)
            {
                Stopwatch clock = Stopwatch.StartNew();
                bool failed = false;
                try
                {
                    PullFrame();
                }
                catch { failed = true; }
                if (token.IsCancellationRequested) break;
                bool state = Connected;
                if (state != oldState)
                {
                    oldState = state;
                    if (StatusChanged != null) StatusChanged();
                    if (state) Emit("HTTP 對端已連線：" + (_baseUrl.Length > 0 ? _baseUrl : ("http://" + Host + ":" + Port)));
                }
                if (token.WaitHandle.WaitOne(failed ? 250 : Math.Max(1, _pullFrameIntervalMs - (int)clock.ElapsedMilliseconds))) break;
            }
        }

        private void InputLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                bool failed = false;
                try { for (int i = 0; i < 8 && !token.IsCancellationRequested && PullInput(); i++) { } }
                catch { failed = true; }
                if (token.WaitHandle.WaitOne(failed ? 250 : 8)) break;
            }
        }

        private void TransferLoop(CancellationToken token)
        {
            DateTime heartbeat = DateTime.MinValue;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow >= heartbeat) { PostJson("/api/v1/heartbeat", new Dictionary<string, object> { { "node", Environment.MachineName }, { "version", Protocol.Version } }, 1000); heartbeat = DateTime.UtcNow.AddSeconds(1); }
                    PullTransferItem();
                }
                catch { }
                if (token.WaitHandle.WaitOne(350)) break;
            }
        }

        public bool Ping(out string message)
        {
            try
            {
                Dictionary<string, object> result = GetJson("/api/v1/health", 1200);
                bool ok = JsonUtil.GetBool(result, "ok", false);
                if (ok) MarkOk();
                message = ok ? "HTTP 8001 正常" : "HTTP 回應格式錯誤";
                return ok;
            }
            catch (Exception ex) { message = ex.Message; return false; }
        }

        public bool PushFrame(byte[] jpeg, ScreenInfo info)
        {
            if (!Connected || jpeg == null || jpeg.Length == 0) return false;
            try
            {
                HttpWebRequest request = CreateRequest("/api/v1/kvm/push/frame", "PUT", 1200);
                request.ContentType = "image/jpeg";
                request.ContentLength = jpeg.Length;
                AddScreenHeaders(request, info);
                using (Stream output = request.GetRequestStream()) output.Write(jpeg, 0, jpeg.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) { }
                MarkOk();
                return true;
            }
            catch { return false; }
        }

        public bool SendInput(Dictionary<string, object> input)
        {
            if (!Connected) return false;
            try
            {
                PostJson("/api/v1/kvm/push/input", input, 800);
                MarkOk();
                return true;
            }
            catch { return false; }
        }

        public string SendJson(object payload)
        {
            PostJson("/api/v1/push/json", payload, 3000);
            MarkOk();
            return Guid.NewGuid().ToString("N");
        }

        public void SendPaths(IEnumerable<string> paths)
        {
            CancellationToken previous = _workerToken;
            _workerToken = _cancellation.Token;
            try
            {
            List<FileEntry> entries = BuildEntries(paths);
            long total = 0;
            foreach (FileEntry entry in entries) total += entry.Size;
            long completed = 0;
            foreach (FileEntry entry in entries)
            {
                long baseCompleted = completed;
                UploadFile(entry, delegate(long current, long fileTotal)
                {
                    if (FileProgress != null) FileProgress(baseCompleted + current, total, entry.RelativePath);
                });
                completed += entry.Size;
            }
            Emit("傳送完成：" + entries.Count + " 個檔案 · " + NetworkUtils.HumanSize(total));
            }
            finally { _workerToken = previous; }
        }

        private void PullFrame()
        {
            HttpWebRequest request = CreateRequest("/api/v1/kvm/pull/frame?after=" + _lastRemoteFrameSeq, "GET", 850);
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.NoContent) { MarkOk(); return; }
                    long seq;
                    _requestToken.ThrowIfCancellationRequested();
                    if (long.TryParse(response.Headers["X-Chao-Frame-Seq"], out seq)) _lastRemoteFrameSeq = seq;
                    ScreenInfo info = ScreenInfoFromResponse(response);
                    using (MemoryStream ms = new MemoryStream())
                    using (Stream input = response.GetResponseStream())
                    {
                        input.CopyTo(ms);
                        byte[] frame = ms.ToArray();
                        _requestToken.ThrowIfCancellationRequested();
                        if (RemoteInfoChanged != null) RemoteInfoChanged(info);
                        if (RemoteFrame != null && frame.Length > 0) RemoteFrame(frame);
                    }
                    MarkOk();
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NoContent) { MarkOk(); return; }
                throw;
            }
        }

        private bool PullInput()
        {
            Dictionary<string, object> result = GetJson("/api/v1/kvm/pull/input?batch=1", 700);
            MarkOk();
            if (!JsonUtil.GetBool(result, "available", false)) return false;
            object raw;
            if (result.TryGetValue("payloads", out raw) && raw is IEnumerable)
            {
                foreach (object item in (IEnumerable)raw) { _requestToken.ThrowIfCancellationRequested(); var payload = item as Dictionary<string, object>; if (payload != null && InputReceived != null) InputReceived(payload); }
                return true;
            }
            Dictionary<string, object> input = result.TryGetValue("payload", out raw) ? raw as Dictionary<string, object> : null;
            _requestToken.ThrowIfCancellationRequested();
            if (input != null && InputReceived != null) InputReceived(input);
            return input != null;
        }

        private void PullTransferItem()
        {
            Dictionary<string, object> next = GetJson("/api/v1/pull/next", 900);
            MarkOk();
            if (!JsonUtil.GetBool(next, "available", false)) return;
            string id = JsonUtil.GetString(next, "id", "");
            string type = JsonUtil.GetString(next, "type", "");
            if (string.IsNullOrWhiteSpace(id)) return;
            if (type == "file")
            {
                string relative = DecodeB64(JsonUtil.GetString(next, "path_b64", ""));
                DownloadFile(id, relative);
                PostJson("/api/v1/pull/ack/" + id, new Dictionary<string, object>(), 1000);
            }
            else if (type == "json")
            {
                object payload;
                next.TryGetValue("json", out payload);
                if (JsonReceived != null) JsonReceived(payload);
                PostJson("/api/v1/pull/ack/" + id, new Dictionary<string, object>(), 1000);
            }
        }

        private void UploadFile(FileEntry entry, Action<long, long> progress)
        {
            HttpWebRequest request = CreateRequest("/api/v1/push/file", "PUT", 10000);
            request.ContentType = "application/octet-stream";
            request.ContentLength = entry.Size;
            request.ReadWriteTimeout = 60000;
            request.Headers["X-Chao-Path-B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.RelativePath));
            byte[] buffer = new byte[Protocol.BufferSize];
            long sent = 0;
            using (Stream output = request.GetRequestStream())
            using (FileStream input = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    sent += read;
                    if (progress != null) progress(sent, entry.Size);
                }
            }
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) { }
            MarkOk();
        }

        private void DownloadFile(string id, string relative)
        {
            string safe = SanitizeRelativePath(relative);
            Directory.CreateDirectory(ReceiveDirectory);
            string target = Path.Combine(ReceiveDirectory, safe);
            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!Overwrite) target = UniquePath(target);
            string part = target + ".chao-part";
            HttpWebRequest request = CreateRequest("/api/v1/pull/file/" + id, "GET", 10000);
            request.ReadWriteTimeout = 60000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = response.ContentLength;
                long current = 0;
                byte[] buffer = new byte[Protocol.BufferSize];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    current += read;
                    if (FileProgress != null) FileProgress(current, total, safe);
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            Emit("接收完成：" + target);
        }

        private Dictionary<string, object> GetJson(string path, int timeout)
        {
            HttpWebRequest request = CreateRequest(path, "GET", timeout);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return JsonUtil.Parse(reader.ReadToEnd());
        }

        private Dictionary<string, object> PostJson(string path, object payload, int timeout)
        {
            byte[] raw = JsonUtil.Utf8(payload);
            HttpWebRequest request = CreateRequest(path, "POST", timeout);
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = raw.Length;
            using (Stream output = request.GetRequestStream()) output.Write(raw, 0, raw.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return JsonUtil.Parse(reader.ReadToEnd());
        }

        private HttpWebRequest CreateRequest(string path, string method, int timeout)
        {
            _requestToken = _workerToken.CanBeCanceled ? _workerToken : _cancellation.Token;
            _requestToken.ThrowIfCancellationRequested();
            string root = _baseUrl.Length > 0 ? _baseUrl : ("http://" + Host + ":" + Port);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(root + path);
            request.Method = method;
            request.Proxy = null;
            request.KeepAlive = false;
            // Scoped to this peer, not global framework defaults. Preserve close-based legacy HTTP.
            request.ServicePoint.Expect100Continue = false;
            request.ServicePoint.UseNagleAlgorithm = false;
            request.ServicePoint.ConnectionLimit = Math.Max(16, request.ServicePoint.ConnectionLimit);
            request.AllowWriteStreamBuffering = false;
            request.Timeout = timeout;
            request.ReadWriteTimeout = Math.Max(timeout, 1000);
            request.UserAgent = "ChaoChaoKvm/" + Protocol.Version;
            if (_accessKey.Length > 0) request.Headers["X-Chao-Access-Key"] = _accessKey;
            if (request.RequestUri != null && request.RequestUri.Host.IndexOf("ngrok", StringComparison.OrdinalIgnoreCase) >= 0)
                request.Headers["ngrok-skip-browser-warning"] = "1";
            _requests[Thread.CurrentThread.ManagedThreadId] = request;
            if (_requestToken.IsCancellationRequested) { request.Abort(); _requestToken.ThrowIfCancellationRequested(); }
            return request;
        }

        private void MarkOk()
        {
            if (_requestToken.IsCancellationRequested) return;
            bool was = Connected;
            _lastOk = DateTime.Now;
            if (!was && StatusChanged != null) StatusChanged();
        }

        private static void AddScreenHeaders(HttpWebRequest request, ScreenInfo info)
        {
            if (info == null) return;
            request.Headers["X-Chao-Screen-Left"] = info.Left.ToString();
            request.Headers["X-Chao-Screen-Top"] = info.Top.ToString();
            request.Headers["X-Chao-Screen-Width"] = info.Width.ToString();
            request.Headers["X-Chao-Screen-Height"] = info.Height.ToString();
            request.Headers["X-Chao-Machine-B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(info.Machine ?? ""));
        }

        private static ScreenInfo ScreenInfoFromResponse(HttpWebResponse response)
        {
            int left = HeaderInt(response, "X-Chao-Screen-Left", 0);
            int top = HeaderInt(response, "X-Chao-Screen-Top", 0);
            int width = HeaderInt(response, "X-Chao-Screen-Width", 1920);
            int height = HeaderInt(response, "X-Chao-Screen-Height", 1080);
            string machine = "REMOTE";
            try
            {
                string encoded = response.Headers["X-Chao-Machine-B64"];
                if (!string.IsNullOrWhiteSpace(encoded)) machine = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch { }
            return new ScreenInfo { Left = left, Top = top, Width = Math.Max(1, width), Height = Math.Max(1, height), Machine = machine };
        }

        private static int HeaderInt(HttpWebResponse response, string name, int fallback)
        {
            int value;
            string raw = response.Headers[name];
            return int.TryParse(raw, out value) ? value : fallback;
        }

        private sealed class FileEntry
        {
            public string FullPath;
            public string RelativePath;
            public long Size;
        }

        private static List<FileEntry> BuildEntries(IEnumerable<string> paths)
        {
            List<FileEntry> result = new List<FileEntry>();
            foreach (string raw in paths)
            {
                if (File.Exists(raw))
                {
                    FileInfo info = new FileInfo(raw);
                    result.Add(new FileEntry { FullPath = raw, RelativePath = info.Name, Size = info.Length });
                }
                else if (Directory.Exists(raw))
                {
                    DirectoryInfo root = new DirectoryInfo(raw);
                    foreach (string file in Directory.GetFiles(raw, "*", SearchOption.AllDirectories))
                    {
                        FileInfo info = new FileInfo(file);
                        string rel = root.Name + Path.DirectorySeparatorChar + file.Substring(root.FullName.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                        result.Add(new FileEntry { FullPath = file, RelativePath = rel, Size = info.Length });
                    }
                }
            }
            return result;
        }

        private static string DecodeB64(string value)
        {
            if (string.IsNullOrEmpty(value)) return "download.bin";
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static string SanitizeRelativePath(string raw)
        {
            string value = (raw ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string[] parts = value.Split(Path.DirectorySeparatorChar);
            List<string> clean = new List<string>();
            foreach (string part in parts) if (!string.IsNullOrEmpty(part) && part != "." && part != "..") clean.Add(part);
            return clean.Count == 0 ? "download.bin" : Path.Combine(clean.ToArray());
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

        public void Dispose() { Stop(); }
    }
}
