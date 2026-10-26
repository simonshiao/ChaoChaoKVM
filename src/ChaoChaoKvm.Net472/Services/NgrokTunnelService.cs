using ChaoChaoKvm.Core;
using System;
using System.Diagnostics;
using System.IO;

namespace ChaoChaoKvm.Services
{
    public sealed class NgrokTunnelService : IDisposable
    {
        private Process _process;
        private readonly object _sync = new object();

        public string PublicUrl { get; private set; }
        public string Detail { get; private set; }
        public bool Running { get { lock (_sync) return _process != null && !_process.HasExited; } }

        public event Action Changed;
        public event Action<string> Log;

        public NgrokTunnelService()
        {
            PublicUrl = "";
            Detail = "ngrok 尚未啟動";
        }

        public string FindNgrok()
        {
            string env = (Environment.GetEnvironmentVariable("NGROK_BIN") ?? "").Trim();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new[]
            {
                env,
                @"D:\AI-CHAOCHAO\ChaoMCPHub\tools\ngrok.exe",
                @"D:\AI-CHAOCHAO\ChaoMCPHub\ngrok.exe",
                string.IsNullOrWhiteSpace(local) ? "" : Path.Combine(local, "Microsoft", "WindowsApps", "ngrok.exe")
            };
            foreach (string candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            return "ngrok.exe";
        }

        public void Start(int targetPort, string fixedDomain)
        {
            lock (_sync)
            {
                StopLocked();
                string exe = FindNgrok();
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = BuildArguments(targetPort, fixedDomain),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                EnsureEnvironment(psi);
                Process process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) HandleLine(e.Data); };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) HandleLine(e.Data); };
                process.Exited += delegate
                {
                    lock (_sync)
                    {
                        if (!ReferenceEquals(_process, process)) return;
                        Detail = process.ExitCode == 0 ? "ngrok 已停止" : "ngrok 已結束，代碼 " + process.ExitCode;
                        PublicUrl = "";
                    }
                    RaiseChanged();
                };
                PublicUrl = "";
                Detail = "ngrok 連線中…";
                if (!process.Start()) throw new InvalidOperationException("無法啟動 ngrok.exe");
                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                RaiseChanged();
            }
        }

        private static string BuildArguments(int port, string fixedDomain)
        {
            // ChaoMCPHub already owns the account's HTTP endpoint. KVM uses a
            // separate TCP tunnel so both products can stay online together.
            return "tcp " + port + " --log=stdout --log-format=json";
        }

        private void HandleLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Log != null) Log("ngrok: " + text);
            try
            {
                var data = JsonUtil.Parse(text);
                string msg = JsonUtil.GetString(data, "msg", "");
                string url = JsonUtil.GetString(data, "url", "");
                if (msg == "started tunnel" && url.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_sync)
                    {
                        PublicUrl = url.TrimEnd('/');
                        Detail = "Online 已啟用";
                    }
                    RaiseChanged();
                }
            }
            catch { }
        }

        private static void EnsureEnvironment(ProcessStartInfo psi)
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(profile)) psi.EnvironmentVariables["USERPROFILE"] = profile;
            if (!string.IsNullOrWhiteSpace(local)) { psi.EnvironmentVariables["LOCALAPPDATA"] = local; psi.EnvironmentVariables["LocalAppData"] = local; }
            if (!string.IsNullOrWhiteSpace(roaming)) psi.EnvironmentVariables["APPDATA"] = roaming;
        }

        private void RaiseChanged() { if (Changed != null) Changed(); }

        public void Stop()
        {
            lock (_sync) StopLocked();
            RaiseChanged();
        }

        private void StopLocked()
        {
            Process process = _process;
            _process = null;
            PublicUrl = "";
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                try { process.Dispose(); } catch { }
            }
            Detail = "ngrok 已停止";
        }

        public void Dispose() { Stop(); }
    }
}
