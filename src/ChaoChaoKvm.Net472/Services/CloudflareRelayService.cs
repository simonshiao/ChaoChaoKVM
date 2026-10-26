using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ChaoChaoKvm.Services
{
    /// <summary>
    /// Carries the existing local HTTP 8001 protocol inside Cloudflare Access WebSockets.
    /// The local listener is loopback-only, so this does not change LAN discovery or port 8001.
    /// </summary>
    public sealed class CloudflareRelayService : IDisposable
    {
        private const int LocalServicePort = 8001;
        private const int MaxFrameBytes = 64 * 1024;
        private const string AccessHeader = "cf-access-token";
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _accessTokenGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, HostStream> _hostStreams = new ConcurrentDictionary<string, HostStream>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<TcpClient, CancellationTokenSource> _clientStreams = new ConcurrentDictionary<TcpClient, CancellationTokenSource>();
        private CancellationTokenSource _hostCancellation;
        private CancellationTokenSource _clientCancellation;
        private ClientWebSocket _hostSocket;
        private TcpListener _clientListener;
        private Task _hostTask;
        private Task _clientAcceptTask;
        private string _accessToken;
        private string _accessTokenHostname;
        private DateTime _accessTokenExpiresUtc;
        private string _detail = "Cloudflare HTTPS 中繼尚未啟動";
        private bool _hostConnected;
        private bool _disposed;

        public event Action Changed;
        public event Action<string> Log;

        public bool HostRunning { get { lock (_sync) return _hostCancellation != null && !_hostCancellation.IsCancellationRequested; } }
        public bool HostConnected { get { lock (_sync) return HostRunning && _hostConnected; } }
        public int ClientCount { get { return _clientStreams.Count + _hostStreams.Count; } }
        public string Detail { get { lock (_sync) return _detail; } }

        public void StartHost(string hostname, string roomId, string secret)
        {
            hostname = CloudflareTunnelService.NormalizeHostname(hostname);
            ValidateRoom(roomId, secret);
            CancellationTokenSource cancellation = new CancellationTokenSource();
            lock (_sync)
            {
                ThrowIfDisposed();
                StopHostLocked();
                _hostCancellation = cancellation;
                _hostConnected = false;
                _detail = "正在連接 Cloudflare HTTPS 中繼…";
                CancellationToken token = cancellation.Token;
                _hostTask = Task.Run(async delegate { try { await RunHostAsync(hostname, roomId, secret, token).ConfigureAwait(false); } finally { cancellation.Dispose(); } });
            }
            RaiseChanged();
        }

        public void StopHost()
        {
            lock (_sync) StopHostLocked();
            RaiseChanged();
        }

        public int StartClient(string hostname, string roomId, string secret)
        {
            hostname = CloudflareTunnelService.NormalizeHostname(hostname);
            ValidateRoom(roomId, secret);
            lock (_sync)
            {
                ThrowIfDisposed();
                StopClientsLocked();
                _clientCancellation = new CancellationTokenSource();
                _clientListener = new TcpListener(IPAddress.Loopback, 0);
                _clientListener.Start(8);
                int port = ((IPEndPoint)_clientListener.LocalEndpoint).Port;
                CancellationToken cancellation = _clientCancellation.Token;
                _clientAcceptTask = Task.Run(delegate { AcceptClientsAsync(hostname, roomId, secret, cancellation); });
                Emit("Cloudflare HTTPS 中繼本機轉接已建立：127.0.0.1:" + port + ".");
                RaiseChanged();
                return port;
            }
        }

        public void StopClients()
        {
            lock (_sync) StopClientsLocked();
            RaiseChanged();
        }

        private async Task RunHostAsync(string hostname, string roomId, string secret, CancellationToken cancellation)
        {
            int attempt = 0;
            while (!cancellation.IsCancellationRequested)
            {
                ClientWebSocket socket = null;
                try
                {
                    string token = await GetAccessTokenAsync(hostname, cancellation).ConfigureAwait(false);
                    socket = CreateSocket(token);
                    socket.Options.SetRequestHeader("X-KVM-Host-Key", secret);
                    Uri endpoint = BuildRelayUri(hostname, roomId, "host");
                    await socket.ConnectAsync(endpoint, cancellation).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (cancellation.IsCancellationRequested) { socket.Abort(); return; }
                        _hostSocket = socket;
                        _hostConnected = true;
                        _detail = "VPN 443 中繼已連線";
                    }
                    attempt = 0;
                    RaiseChanged();
                    Emit("Cloudflare Access HTTPS 中繼已連線；僅轉送到本機 HTTP 8001。");
                    await ReceiveHostAsync(socket, cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!cancellation.IsCancellationRequested)
                    {
                        lock (_sync) { _hostConnected = false; _detail = "HTTPS 中繼連線中斷，正在重試"; }
                        Emit("Cloudflare HTTPS 中繼連線失敗：" + SafeMessage(ex));
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_hostSocket, socket)) _hostSocket = null;
                        if (!cancellation.IsCancellationRequested) _hostConnected = false;
                    }
                    if (socket != null) { try { socket.Abort(); } catch { } socket.Dispose(); }
                    CloseHostStreams();
                    RaiseChanged();
                }
                int delay = Math.Min(30000, 1000 * (1 << Math.Min(5, attempt++)));
                if (cancellation.WaitHandle.WaitOne(delay)) break;
            }
        }

        private async Task ReceiveHostAsync(ClientWebSocket socket, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                ReceivedMessage message = await ReceiveAsync(socket, cancellation).ConfigureAwait(false);
                if (message.Type == WebSocketMessageType.Close) break;
                if (message.Type == WebSocketMessageType.Text)
                {
                    string control = Encoding.UTF8.GetString(message.Data);
                    string[] parts = control.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[0] == "OPEN" && IsRoomId(parts[1]))
                    {
                        Task.Run(delegate { OpenHostStreamAsync(parts[1], socket, cancellation); });
                    }
                    else if (parts.Length >= 2 && parts[0] == "CLOSE" && IsRoomId(parts[1]))
                    {
                        HostStream stream;
                        if (_hostStreams.TryRemove(parts[1], out stream)) stream.Dispose();
                    }
                    else Emit("Cloudflare HTTPS 中繼收到未知控制訊息。");
                    continue;
                }
                if (message.Data.Length < 16) { Emit("Cloudflare HTTPS 中繼資料框格式錯誤。"); continue; }
                string id = BytesToHex(message.Data, 0, 16);
                HostStream hostStream;
                if (_hostStreams.TryGetValue(id, out hostStream))
                {
                    try { await hostStream.Stream.WriteAsync(message.Data, 16, message.Data.Length - 16, cancellation).ConfigureAwait(false); }
                    catch { HostStream removed; if (_hostStreams.TryRemove(id, out removed)) removed.Dispose(); }
                }
            }
        }

        private async Task OpenHostStreamAsync(string id, ClientWebSocket socket, CancellationToken cancellation)
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_hostSocket, socket)) return;
            HostStream hostStream = null;
            bool failed = false;
            try
            {
                TcpClient tcp = new TcpClient(AddressFamily.InterNetwork);
                await tcp.ConnectAsync(IPAddress.Loopback, LocalServicePort).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested) { tcp.Close(); return; }
                hostStream = new HostStream(id, tcp);
                if (!_hostStreams.TryAdd(id, hostStream)) { hostStream.Dispose(); return; }
                await SendHostControlAsync(socket, "OPENED " + id, cancellation).ConfigureAwait(false);
                await PumpHostStreamAsync(hostStream, socket, cancellation).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (hostStream != null) { HostStream removed; if (_hostStreams.TryRemove(id, out removed)) removed.Dispose(); }
                failed = true;
            }
            if (failed) try { await SendHostControlAsync(socket, "FAILED " + id + " 本機 KVM 服務無法連線", cancellation).ConfigureAwait(false); } catch { }
        }

        private async Task PumpHostStreamAsync(HostStream stream, ClientWebSocket socket, CancellationToken cancellation)
        {
            byte[] buffer = new byte[MaxFrameBytes];
            try
            {
                while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    int read = await stream.Stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false);
                    if (read == 0) break;
                    byte[] frame = new byte[16 + read];
                    HexToBytes(stream.Id, frame, 0);
                    Buffer.BlockCopy(buffer, 0, frame, 16, read);
                    await SendHostBinaryAsync(socket, frame, cancellation).ConfigureAwait(false);
                }
            }
            catch { }
            HostStream removed;
            if (_hostStreams.TryRemove(stream.Id, out removed)) removed.Dispose();
            try { await SendHostControlAsync(socket, "CLOSE " + stream.Id, cancellation).ConfigureAwait(false); } catch { }
        }

        private async Task AcceptClientsAsync(string hostname, string roomId, string secret, CancellationToken cancellation)
        {
            TcpListener listener;
            lock (_sync) listener = _clientListener;
            if (listener == null) return;
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (cancellation.IsCancellationRequested) { client.Close(); break; }
                    CancellationTokenSource streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    if (!_clientStreams.TryAdd(client, streamCancellation)) { streamCancellation.Dispose(); client.Close(); continue; }
                    Task.Run(delegate { RunClientStreamAsync(client, hostname, roomId, secret, streamCancellation); });
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (cancellation.IsCancellationRequested) break; }
                catch (Exception ex) { if (!cancellation.IsCancellationRequested) Emit("Cloudflare HTTPS 本機轉接失敗：" + SafeMessage(ex)); }
            }
        }

        private async Task RunClientStreamAsync(TcpClient tcp, string hostname, string roomId, string secret, CancellationTokenSource streamCancellation)
        {
            ClientWebSocket socket = null;
            CancellationToken cancellation = streamCancellation.Token;
            try
            {
                string token = await GetAccessTokenAsync(hostname, cancellation).ConfigureAwait(false);
                socket = CreateSocket(token);
                socket.Options.SetRequestHeader("X-KVM-Pair-Key", secret);
                await socket.ConnectAsync(BuildRelayUri(hostname, roomId, "client"), cancellation).ConfigureAwait(false);
                ReceivedMessage ready = await ReceiveAsync(socket, cancellation).ConfigureAwait(false);
                string response = ready.Type == WebSocketMessageType.Text ? Encoding.UTF8.GetString(ready.Data) : "";
                if (response.StartsWith("FAILED ", StringComparison.Ordinal)) throw new IOException("遠端 KVM 服務目前無法連線。");
                if (!response.StartsWith("OPENED ", StringComparison.Ordinal)) throw new IOException("中繼工作階段尚未就緒。");
                NetworkStream stream = tcp.GetStream();
                Task fromLocal = PumpClientToSocketAsync(stream, socket, cancellation);
                Task toLocal = PumpSocketToClientAsync(socket, stream, cancellation);
                await Task.WhenAny(fromLocal, toLocal).ConfigureAwait(false);
                try { streamCancellation.Cancel(); } catch { }
                await ObserveAsync(fromLocal).ConfigureAwait(false);
                await ObserveAsync(toLocal).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!streamCancellation.IsCancellationRequested) Emit("Cloudflare HTTPS 遠端串流中斷：" + SafeMessage(ex));
            }
            finally
            {
                if (socket != null) { try { socket.Abort(); } catch { } socket.Dispose(); }
                try { streamCancellation.Cancel(); } catch { }
                tcp.Close();
                CancellationTokenSource removed;
                if (_clientStreams.TryRemove(tcp, out removed)) removed.Dispose();
                RaiseChanged();
            }
        }

        private static async Task PumpClientToSocketAsync(NetworkStream stream, ClientWebSocket socket, CancellationToken cancellation)
        {
            byte[] buffer = new byte[MaxFrameBytes];
            while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false);
                if (read == 0) break;
                await socket.SendAsync(new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, true, cancellation).ConfigureAwait(false);
            }
        }

        private static async Task PumpSocketToClientAsync(ClientWebSocket socket, NetworkStream stream, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                ReceivedMessage message = await ReceiveAsync(socket, cancellation).ConfigureAwait(false);
                if (message.Type == WebSocketMessageType.Close) break;
                if (message.Type != WebSocketMessageType.Binary) throw new IOException("中繼資料格式錯誤。");
                await stream.WriteAsync(message.Data, 0, message.Data.Length, cancellation).ConfigureAwait(false);
            }
        }

        private async Task<string> GetAccessTokenAsync(string hostname, CancellationToken cancellation)
        {
            await _accessTokenGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (string.Equals(_accessTokenHostname, hostname, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_accessToken) && DateTime.UtcNow < _accessTokenExpiresUtc.AddMinutes(-2)) return _accessToken;
                string exe = FindCloudflared();
                string output = await RunCloudflaredAsync(exe, "access token -app=https://" + hostname, TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
                string token = ExtractJwt(output);
                if (token.Length == 0)
                {
                    SetDetail("請在自動開啟的瀏覽器完成 Cloudflare 登入…");
                    Emit("首次連線需要 Cloudflare Access 登入，已開啟瀏覽器；完成登入後會自動繼續。" );
                    await RunCloudflaredAsync(exe, "access login https://" + hostname, TimeSpan.FromMinutes(10), cancellation).ConfigureAwait(false);
                    output = await RunCloudflaredAsync(exe, "access token -app=https://" + hostname, TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
                    token = ExtractJwt(output);
                    if (token.Length == 0) throw new InvalidOperationException("Cloudflare 登入尚未完成，請重試中繼連線。");
                }
                _accessToken = token;
                _accessTokenHostname = hostname;
                _accessTokenExpiresUtc = ReadTokenExpiry(token);
                return token;
            }
            finally { _accessTokenGate.Release(); }
        }

        private static async Task<string> RunCloudflaredAsync(string exe, string arguments, TimeSpan timeout, CancellationToken cancellation)
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            using (Process process = new Process { StartInfo = start })
            {
                cancellation.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException("無法啟動 Cloudflare Access 登入工具。");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                Task exited = Task.Run(() => process.WaitForExit());
                Task deadline = Task.Delay(timeout, cancellation);
                Task completed = await Task.WhenAny(exited, deadline).ConfigureAwait(false);
                if (completed != exited)
                {
                    try { process.Kill(); } catch { }
                    cancellation.ThrowIfCancellationRequested();
                    throw new TimeoutException("Cloudflare Access 登入等待逾時；請再次啟動中繼並完成瀏覽器登入。");
                }
                await exited.ConfigureAwait(false);
                string output = await stdout.ConfigureAwait(false);
                string error = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0 && arguments.StartsWith("access login ", StringComparison.Ordinal))
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Cloudflare Access 登入失敗，請重試。" : "Cloudflare Access 登入失敗，請確認瀏覽器登入已完成後重試。");
                return output;
            }
        }

        private void SetDetail(string detail)
        {
            lock (_sync)
            {
                bool hostActive = _hostCancellation != null && !_hostCancellation.IsCancellationRequested;
                bool clientActive = _clientCancellation != null && !_clientCancellation.IsCancellationRequested;
                if (!hostActive && !clientActive) return;
                _detail = detail;
            }
            RaiseChanged();
        }

        private static string ExtractJwt(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return "";
            MatchCollection matches = Regex.Matches(output, @"(?m)^\s*(eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)\s*$");
            return matches.Count == 0 ? "" : matches[matches.Count - 1].Groups[1].Value;
        }

        private static DateTime ReadTokenExpiry(string token)
        {
            try
            {
                string[] parts = token.Split('.');
                if (parts.Length != 3) return DateTime.UtcNow.AddMinutes(15);
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
                Dictionary<string, object> data = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                object exp;
                if (data != null && data.TryGetValue("exp", out exp)) return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(exp)).UtcDateTime;
            }
            catch { }
            return DateTime.UtcNow.AddMinutes(15);
        }

        private static ClientWebSocket CreateSocket(string accessToken)
        {
            ClientWebSocket socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.SetRequestHeader(AccessHeader, accessToken);
            IWebProxy proxy = CurrentUserProxy();
            if (proxy != null) socket.Options.Proxy = proxy;
            return socket;
        }

        private static IWebProxy CurrentUserProxy()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key == null || Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 1) return null;
                    string value = Convert.ToString(key.GetValue("ProxyServer", ""));
                    if (string.IsNullOrWhiteSpace(value)) return null;
                    string address = value.Trim();
                    string[] entries = address.Split(';');
                    foreach (string entry in entries)
                    {
                        string candidate = entry.Trim();
                        if (candidate.StartsWith("https=", StringComparison.OrdinalIgnoreCase)) { address = candidate.Substring(6).Trim(); break; }
                        if (candidate.StartsWith("http=", StringComparison.OrdinalIgnoreCase)) address = candidate.Substring(5).Trim();
                    }
                    if (!address.Contains("://")) address = "http://" + address;
                    Uri uri;
                    return Uri.TryCreate(address, UriKind.Absolute, out uri) ? new WebProxy(uri) : null;
                }
            }
            catch { return null; }
        }

        private static string FindCloudflared()
        {
            string env = (Environment.GetEnvironmentVariable("CLOUDFLARED_BIN") ?? "").Trim();
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cloudflared.exe");
            string programFiles = (Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "").Trim();
            string installed = string.IsNullOrWhiteSpace(programFiles) ? "" : Path.Combine(programFiles, "cloudflared", "cloudflared.exe");
            foreach (string candidate in new[] { env, local, installed })
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            return "cloudflared.exe";
        }

        private static Uri BuildRelayUri(string hostname, string roomId, string role)
        {
            return new UriBuilder("wss", hostname)
            {
                Path = "/relay",
                Query = "room=" + Uri.EscapeDataString(roomId) + "&role=" + Uri.EscapeDataString(role)
            }.Uri;
        }

        private static async Task SendHostControlAsync(ClientWebSocket socket, string text, CancellationToken cancellation)
        {
            // Host control and data messages share a WebSocket; serialize sends to preserve framing.
            await HostSendGates.For(socket).WaitAsync(cancellation).ConfigureAwait(false);
            try { await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false); }
            finally { HostSendGates.Release(socket); }
        }

        private static async Task SendHostBinaryAsync(ClientWebSocket socket, byte[] frame, CancellationToken cancellation)
        {
            await HostSendGates.For(socket).WaitAsync(cancellation).ConfigureAwait(false);
            try { await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, cancellation).ConfigureAwait(false); }
            finally { HostSendGates.Release(socket); }
        }

        private static class HostSendGates
        {
            private static readonly ConcurrentDictionary<ClientWebSocket, SemaphoreSlim> Gates = new ConcurrentDictionary<ClientWebSocket, SemaphoreSlim>();
            public static SemaphoreSlim For(ClientWebSocket socket) { return Gates.GetOrAdd(socket, delegate { return new SemaphoreSlim(1, 1); }); }
            public static void Release(ClientWebSocket socket) { SemaphoreSlim gate; if (Gates.TryGetValue(socket, out gate)) gate.Release(); }
        }

        private static async Task<ReceivedMessage> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
        {
            byte[] buffer = new byte[MaxFrameBytes + 16];
            using (MemoryStream memory = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return new ReceivedMessage { Type = WebSocketMessageType.Close, Data = new byte[0] };
                    if (memory.Length + result.Count > MaxFrameBytes + 16) throw new IOException("Cloudflare relay frame exceeded the size limit.");
                    memory.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                return new ReceivedMessage { Type = result.MessageType, Data = memory.ToArray() };
            }
        }

        private static string BytesToHex(byte[] bytes, int offset, int count)
        {
            StringBuilder builder = new StringBuilder(count * 2);
            for (int index = 0; index < count; index++) builder.Append(bytes[offset + index].ToString("x2"));
            return builder.ToString();
        }

        private static void HexToBytes(string value, byte[] destination, int offset)
        {
            for (int index = 0; index < 16; index++) destination[offset + index] = Convert.ToByte(value.Substring(index * 2, 2), 16);
        }

        private static bool IsRoomId(string value) { return Regex.IsMatch(value ?? "", @"^[a-f0-9]{32}$"); }

        private static void ValidateRoom(string roomId, string secret)
        {
            if (!IsRoomId(roomId)) throw new InvalidOperationException("Cloudflare 中繼房間識別碼格式不正確。");
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 40 || secret.Length > 80) throw new InvalidOperationException("Cloudflare 中繼金鑰格式不正確。");
        }

        private static string SafeMessage(Exception ex)
        {
            if (ex is WebSocketException) return "無法完成 HTTPS WebSocket 握手，請檢查 Access 政策與 VPN 443 路徑。";
            if (ex is TimeoutException) return "連線逾時。";
            if (ex is OperationCanceledException) return "已取消。";
            return string.IsNullOrWhiteSpace(ex.Message) ? "連線失敗。" : ex.Message;
        }

        private static string NewSecret()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static async Task DelayAsync(int milliseconds, CancellationToken cancellation)
        {
            await Task.Delay(milliseconds, cancellation).ConfigureAwait(false);
        }

        private static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); } catch { }
        }

        private void StopHostLocked()
        {
            CancellationTokenSource cancellation = _hostCancellation;
            _hostCancellation = null;
            _hostConnected = false;
            _detail = "Cloudflare HTTPS 中繼已停止";
            if (cancellation != null) { try { cancellation.Cancel(); } catch { } }
            if (_hostSocket != null) { try { _hostSocket.Abort(); } catch { } _hostSocket = null; }
            CloseHostStreams();
        }

        private void StopClientsLocked()
        {
            CancellationTokenSource cancellation = _clientCancellation;
            _clientCancellation = null;
            if (cancellation != null) { try { cancellation.Cancel(); } catch { } cancellation.Dispose(); }
            if (_clientListener != null) { try { _clientListener.Stop(); } catch { } _clientListener = null; }
            foreach (KeyValuePair<TcpClient, CancellationTokenSource> stream in _clientStreams)
            {
                try { stream.Value.Cancel(); } catch { }
                try { stream.Key.Close(); } catch { }
            }
        }

        private void CloseHostStreams()
        {
            foreach (KeyValuePair<string, HostStream> stream in _hostStreams)
            {
                HostStream removed;
                if (_hostStreams.TryRemove(stream.Key, out removed)) removed.Dispose();
            }
        }

        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException("CloudflareRelayService"); }
        private void RaiseChanged() { Action handler = Changed; if (handler != null) handler(); }
        private void Emit(string message) { Action<string> handler = Log; if (handler != null) handler(message); }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                StopHostLocked();
                StopClientsLocked();
            }
        }

        private sealed class HostStream : IDisposable
        {
            private readonly TcpClient _client;
            public HostStream(string id, TcpClient client) { Id = id; _client = client; Stream = client.GetStream(); }
            public string Id { get; private set; }
            public NetworkStream Stream { get; private set; }
            public void Dispose() { try { Stream.Dispose(); } catch { } try { _client.Close(); } catch { } }
        }

        private sealed class ReceivedMessage
        {
            public WebSocketMessageType Type;
            public byte[] Data;
        }
    }
}
