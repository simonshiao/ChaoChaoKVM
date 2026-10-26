using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ChaoChaoKvm.Services
{
    public sealed class CloudflareTunnelService : IDisposable
    {
        private readonly object _sync = new object();
        private readonly List<Process> _clientProcesses = new List<Process>();
        private Process _hostProcess;
        private string _detail = "Cloudflare Tunnel 尚未啟動";
        private bool _connected;
        private bool _disposed;

        public event Action Changed;
        public event Action<string> Log;

        public bool Running { get { lock (_sync) return _hostProcess != null && !_hostProcess.HasExited; } }
        public bool Connected { get { lock (_sync) return Running && _connected; } }
        public int ClientCount { get { lock (_sync) return _clientProcesses.Count; } }
        public string Detail { get { lock (_sync) return _detail; } }

        public void StartHost(string token)
        {
            token = (token ?? "").Trim();
            if (token.Length < 40) throw new InvalidOperationException("請貼上 Cloudflare Dashboard 提供的 Tunnel Token。");
            lock (_sync)
            {
                StopHostLocked();
                string exe = FindCloudflared();
                string tokenFile = Path.Combine(Path.GetTempPath(), "chaokvm-tunnel-" + Guid.NewGuid().ToString("N") + ".token");
                File.WriteAllText(tokenFile, token);
                Process process = CreateProcess(exe, "tunnel run --token-file \"" + tokenFile + "\"");
                AttachHostEvents(process);
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("無法啟動 cloudflared。");
                    _hostProcess = process;
                    _connected = false;
                    _detail = "Cloudflare Tunnel 連線中…";
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(1500);
                        try { if (File.Exists(tokenFile)) File.Delete(tokenFile); } catch { }
                    });
                }
                catch
                {
                    try { if (File.Exists(tokenFile)) File.Delete(tokenFile); } catch { }
                    process.Dispose();
                    throw;
                }
            }
            RaiseChanged();
        }

        public int StartAccess(string hostname)
        {
            hostname = NormalizeHostname(hostname);
            int port = ReserveLocalPort();
            string exe = FindCloudflared();
            Process process = CreateProcess(exe, "access tcp --hostname \"" + hostname + "\" --url 127.0.0.1:" + port + " --loglevel warn");
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Emit("cloudflared access: " + e.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Emit("cloudflared access: " + e.Data); };
            process.Exited += delegate
            {
                lock (_sync) _clientProcesses.Remove(process);
                Emit("Cloudflare Access 轉接已結束。");
                RaiseChanged();
            };
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("CloudflareTunnelService");
                if (!process.Start()) throw new InvalidOperationException("無法啟動 cloudflared Access 轉接。");
                _clientProcesses.Add(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            Emit("已啟動 Cloudflare Access 轉接：" + hostname + " → 127.0.0.1:" + port + "。若尚未登入，cloudflared 會開啟瀏覽器要求驗證。");
            RaiseChanged();
            return port;
        }

        public void StopHost()
        {
            lock (_sync) StopHostLocked();
            RaiseChanged();
        }

        public void StopClients()
        {
            List<Process> processes;
            lock (_sync) { processes = new List<Process>(_clientProcesses); _clientProcesses.Clear(); }
            foreach (Process process in processes) StopProcess(process);
            RaiseChanged();
        }

        private void AttachHostEvents(Process process)
        {
            process.OutputDataReceived += HandleHostLine;
            process.ErrorDataReceived += HandleHostLine;
            process.Exited += delegate
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(_hostProcess, process)) return;
                    _hostProcess = null;
                    _connected = false;
                    _detail = "Cloudflare Tunnel 已停止";
                }
                RaiseChanged();
            };
        }

        private void HandleHostLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string line = e.Data;
            if (line.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Registered tunnel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                lock (_sync) { _connected = true; _detail = "Cloudflare Tunnel 已連線"; }
                RaiseChanged();
            }
            else if (line.IndexOf("ERR", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                lock (_sync) { if (!_connected) _detail = "Tunnel 啟動遇到問題，請查看紀錄"; }
            }
            Emit("cloudflared: " + line);
        }

        private void StopHostLocked()
        {
            Process process = _hostProcess;
            _hostProcess = null;
            _connected = false;
            _detail = "Cloudflare Tunnel 已停止";
            if (process != null) StopProcess(process);
        }

        private static Process CreateProcess(string exe, string arguments)
        {
            return new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                },
                EnableRaisingEvents = true
            };
        }

        private static string FindCloudflared()
        {
            string env = (Environment.GetEnvironmentVariable("CLOUDFLARED_BIN") ?? "").Trim();
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cloudflared.exe");
            string programFiles = (Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "").Trim();
            string installed = string.IsNullOrWhiteSpace(programFiles) ? "" : Path.Combine(programFiles, "cloudflared", "cloudflared.exe");
            string[] candidates = new[] { env, local, installed };
            foreach (string candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            return "cloudflared.exe";
        }

        private static int ReserveLocalPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static string NormalizeHostname(string hostname)
        {
            hostname = (hostname ?? "").Trim().TrimEnd('.');
            Uri uri;
            if (hostname.Length == 0 || hostname.Contains("/") || hostname.Contains("@") ||
                !Uri.TryCreate("https://" + hostname, UriKind.Absolute, out uri) ||
                !string.Equals(uri.Host, hostname, StringComparison.OrdinalIgnoreCase) ||
                !hostname.EndsWith(".chaochao.win", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("請輸入 chaochao.win 底下的 Cloudflare 公開主機名稱，例如 kvm.chaochao.win。");
            return hostname.ToLowerInvariant();
        }

        private static void StopProcess(Process process)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); } catch { }
            try { process.Dispose(); } catch { }
        }

        private void Emit(string text) { Action<string> handler = Log; if (handler != null) handler(text); }
        private void RaiseChanged() { Action handler = Changed; if (handler != null) handler(); }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                StopHostLocked();
            }
            StopClients();
        }
    }
}
