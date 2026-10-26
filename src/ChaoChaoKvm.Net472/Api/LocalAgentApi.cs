using ChaoChaoKvm.Core;
using ChaoChaoKvm.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ChaoChaoKvm.Api
{
    public sealed class LocalAgentApi : IDisposable
    {
        private readonly KvmPeerManager _manager;
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        public int Port { get; private set; }

        public LocalAgentApi(KvmPeerManager manager)
        {
            _manager = manager;
            int port;
            string raw = Environment.GetEnvironmentVariable("CHAO_KVM_API_PORT");
            Port = int.TryParse(raw, out port) && port > 1024 && port <= 65535 ? port : (BuildFlavor.IsOnline ? Protocol.DefaultAgentPort + 1 : Protocol.DefaultAgentPort);
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start(8);
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ChaoKvmAgentApi" };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                }
                catch (SocketException) { if (!_running) break; }
                catch { }
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.AutoFlush = true;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Dictionary<string, object> reply;
                    try { reply = Dispatch(JsonUtil.Parse(line)); }
                    catch (Exception ex) { reply = new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } }; }
                    writer.WriteLine(JsonUtil.Serialize(reply));
                }
            }
        }

        private Dictionary<string, object> Dispatch(Dictionary<string, object> request)
        {
            string cmd = JsonUtil.GetString(request, "cmd", "").ToLowerInvariant();
            if (cmd == "status") return Ok("status", _manager.Snapshot());
            if (cmd == "connect")
            {
                string ip = JsonUtil.GetString(request, "ip", JsonUtil.GetString(request, "host", ""));
                _manager.SetActivePeer(ip);
                return Ok("peer_ip", ip);
            }
            if (cmd == "retry") { _manager.RetryNow(); return Ok(null, null); }
            if (cmd == "ping_peer")
            {
                string message;
                bool ok = _manager.Ping(out message);
                return new Dictionary<string, object> { { "ok", ok }, { "message", message } };
            }
            if (cmd == "allow_control")
            {
                bool enabled = JsonUtil.GetBool(request, "enabled", true);
                _manager.AllowRemoteControl = enabled;
                return Ok("enabled", enabled);
            }
            if (cmd == "input")
            {
                object raw;
                Dictionary<string, object> input = request.TryGetValue("payload", out raw) ? raw as Dictionary<string, object> : null;
                if (input == null) throw new InvalidDataException("缺少 payload");
                _manager.SendInput(input);
                return Ok(null, null);
            }
            if (cmd == "send_json")
            {
                object payload;
                request.TryGetValue("payload", out payload);
                return Ok("message_id", _manager.SendJson(payload));
            }
            if (cmd == "send_file")
            {
                object raw;
                List<string> paths = new List<string>();
                if (request.TryGetValue("paths", out raw) && raw != null)
                {
                    string one = raw as string;
                    if (one != null) paths.Add(one);
                    else
                    {
                        System.Collections.IEnumerable many = raw as System.Collections.IEnumerable;
                        if (many != null)
                            foreach (object item in many) if (item != null) paths.Add(Convert.ToString(item));
                    }
                }
                return Ok("transfer_id", _manager.SendPaths(paths));
            }
            if (cmd == "set_receive_dir")
            {
                string path = JsonUtil.GetString(request, "path", "");
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("缺少 path");
                bool overwrite = JsonUtil.GetBool(request, "overwrite", false);
                _manager.ConfigureTransfer(path, overwrite);
                return Ok("path", path);
            }
            if (cmd == "help") return Ok("commands", new[] { "status", "connect", "retry", "ping_peer", "allow_control", "input", "send_json", "send_file", "set_receive_dir" });
            return new Dictionary<string, object> { { "ok", false }, { "error", "unknown_command:" + cmd } };
        }

        private static Dictionary<string, object> Ok(string key, object value)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["ok"] = true;
            if (!string.IsNullOrEmpty(key)) data[key] = value;
            return data;
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }
    }
}
