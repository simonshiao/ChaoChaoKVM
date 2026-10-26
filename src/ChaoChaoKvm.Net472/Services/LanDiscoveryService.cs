using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ChaoChaoKvm.Services
{
    public sealed class LanPeerAddress
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        internal DateTime SeenUtc;
    }

    // Discovery contains public location information only. It never grants peer trust.
    public sealed class LanDiscoveryService : IDisposable
    {
        public const int DefaultDiscoveryPort = 45890;
        private const string Marker = "CHAO_KVM_DISCOVERY_V1";
        private readonly string _id, _name;
        private readonly int _httpPort, _discoveryPort;
        private readonly IPAddress _bindAddress;
        private readonly Func<string, bool> _isLocal;
        private readonly object _sync = new object(), _sendSync = new object();
        private readonly Dictionary<string, LanPeerAddress> _peers = new Dictionary<string, LanPeerAddress>();
        private readonly Dictionary<string, DateTime> _replied = new Dictionary<string, DateTime>();
        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _running;
        private DateTime _nextSearchUtc;

        public LanDiscoveryService(string id, string name, int httpPort)
            : this(id, name, httpPort, DefaultDiscoveryPort, IPAddress.Any, NetworkUtils.IsLocalAddress) { }

        internal LanDiscoveryService(string id, string name, int httpPort, int discoveryPort, IPAddress bindAddress, Func<string, bool> isLocal)
        {
            _id = id; _name = name; _httpPort = httpPort; _discoveryPort = discoveryPort;
            _bindAddress = bindAddress; _isLocal = isLocal;
        }

        public bool Running { get { return _running; } }
        internal int BoundPort { get { return _udp == null ? 0 : ((IPEndPoint)_udp.Client.LocalEndPoint).Port; } }

        public void Start()
        {
            if (_running) return;
            UdpClient socket = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                socket.ExclusiveAddressUse = true;
                socket.Client.Bind(new IPEndPoint(_bindAddress, _discoveryPort));
                socket.EnableBroadcast = true;
                socket.Client.ReceiveTimeout = 500;
                socket.Ttl = 1;
                _udp = socket; _running = true;
                _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "ChaoKvmLanDiscovery" };
                _thread.Start();
            }
            catch { socket.Close(); throw; }
        }

        public void Search()
        {
            lock (_sync) _nextSearchUtc = DateTime.UtcNow.AddSeconds(10);
            if (!_running) return;
            foreach (IPEndPoint target in BroadcastTargets()) SendQuery(target);
        }

        internal void SendQuery(IPEndPoint endpoint) { Send("query", endpoint); }

        private void Send(string kind, IPEndPoint endpoint)
        {
            byte[] data = JsonUtil.Utf8(new { service = Marker, kind = kind, id = _id, name = _name, port = _httpPort });
            lock (_sendSync)
            {
                try { if (_running) _udp.Send(data, data.Length, endpoint); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    bool search;
                    lock (_sync) search = DateTime.UtcNow >= _nextSearchUtc;
                    if (search) Search();
                    IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udp.Receive(ref source);
                    if (data.Length > 2048 || !NetworkUtils.IsPrivateOrLinkLocal(source.Address.ToString())) continue;
                    Dictionary<string, object> packet = JsonUtil.Parse(Encoding.UTF8.GetString(data));
                    string kind = JsonUtil.GetString(packet, "kind", ""), id = JsonUtil.GetString(packet, "id", "");
                    string name = JsonUtil.GetString(packet, "name", "").Trim();
                    Guid parsed;
                    int port = JsonUtil.GetInt(packet, "port", 0);
                    if (JsonUtil.GetString(packet, "service", "") != Marker || (kind != "query" && kind != "reply") ||
                        !Guid.TryParseExact(id, "N", out parsed) || id == _id || name.Length == 0 || name.Length > 128 ||
                        name.Any(char.IsControl) || port < 1025 || port > 65535) continue;
                    string ip = source.Address.ToString();
                    if (!_isLocal(ip))
                    {
                        lock (_sync)
                        {
                            Expire();
                            if (_peers.Count < 128 || _peers.ContainsKey(id + "/" + ip))
                                _peers[id + "/" + ip] = new LanPeerAddress { Id = id, Name = name, Ip = ip, Port = port, SeenUtc = DateTime.UtcNow };
                        }
                    }
                    if (kind == "query")
                    {
                        bool reply;
                        lock (_sync)
                        {
                            DateTime last;
                            reply = !_replied.TryGetValue(ip, out last) || DateTime.UtcNow - last > TimeSpan.FromSeconds(1);
                            if (reply) { if (_replied.Count > 256) _replied.Clear(); _replied[ip] = DateTime.UtcNow; }
                        }
                        if (reply) Send("reply", source);
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { /* Ignore unrelated or malformed local datagrams. */ }
            }
        }

        private void Expire()
        {
            foreach (string key in _peers.Where(p => DateTime.UtcNow - p.Value.SeenUtc > TimeSpan.FromSeconds(30)).Select(p => p.Key).ToArray()) _peers.Remove(key);
        }

        internal LanPeerAddress[] Peers()
        {
            LanPeerAddress[] peers;
            lock (_sync) { Expire(); peers = _peers.Values.ToArray(); }
            UnicastIPAddressInformation[] networks = PhysicalAddresses().ToArray();
            return peers.OrderBy(p => PhysicalSubnet(p.Ip, networks) ? 0 : 1).ThenBy(p => p.Ip).ToArray();
        }

        public object[] Snapshot()
        {
            return Peers().Where(p => p.Port == _httpPort).GroupBy(p => p.Id).Select(g => g.First())
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => (object)new { id = p.Id, name = p.Name, ip = p.Ip, port = p.Port }).ToArray();
        }

        public Task<LanPeerAddress> ResolveAsync(string target, CancellationToken cancellation)
        {
            return Task.Run(delegate
            {
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(8000);
                    try { return Resolve(target, deadline.Token); }
                    catch (OperationCanceledException)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("尋找電腦逾時。請確認對方已開啟 KVM，也可直接輸入 IP 位址。");
                    }
                }
            }, cancellation);
        }

        internal LanPeerAddress Resolve(string target, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            target = (target ?? "").Trim();
            string literal;
            if (TryIPv4(target, out literal))
            {
                ValidateDestination(literal);
                return new LanPeerAddress { Ip = literal, Port = _httpPort, Name = "" };
            }
            if (!ValidName(target)) throw new InvalidOperationException("請輸入電腦名稱或完整 IPv4 位址，例如 Home-1 或 192.168.1.20。");
            string normalizedName = target.TrimEnd('.');
            string shortName = normalizedName.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? normalizedName.Substring(0, normalizedName.Length - 6) : normalizedName;
            if (string.Equals(shortName, _name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("這個名稱與本機電腦名稱相同，請從附近清單選擇另一台電腦，或輸入對方 IP。");
            Search();
            Stopwatch wait = Stopwatch.StartNew();
            while (_running && wait.ElapsedMilliseconds < 1200) { cancellation.ThrowIfCancellationRequested(); cancellation.WaitHandle.WaitOne(60); }
            LanPeerAddress[] found = Peers().Where(p => string.Equals(p.Name, shortName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Select(p => p.Id).Distinct().Count() > 1) throw new InvalidOperationException("找到多台同名電腦，請從「附近的電腦」選擇位址後連線。");
            if (found.Length > 0 && found.All(p => p.Port != _httpPort)) throw new InvalidOperationException("找到這台電腦，但它使用不同的連線埠。請讓兩端使用相同的 HTTP 連線埠。");
            List<LanPeerAddress> candidates = found.Where(p => p.Port == _httpPort).ToList();
            LanPeerAddress reachable = Reachable(candidates, cancellation);
            if (reachable != null) return reachable;
            candidates.Clear();
            foreach (string host in new[] { target, target.IndexOf('.') < 0 ? target + ".local" : "" }.Where(h => h.Length > 0))
            {
                foreach (string ip in ResolveDns(host, cancellation))
                    if (NetworkUtils.IsPrivateOrLinkLocal(ip) && !_isLocal(ip) && !candidates.Any(p => p.Ip == ip))
                        candidates.Add(new LanPeerAddress { Ip = ip, Name = target, Port = _httpPort });
                reachable = Reachable(candidates, cancellation);
                if (reachable != null) return reachable;
                candidates.Clear();
            }
            throw new InvalidOperationException("找不到可連線的「" + target + "」。請確認對方已開啟 KVM 並連到同一個區域網路，也可直接輸入 IP 位址。");
        }

        private static LanPeerAddress Reachable(IEnumerable<LanPeerAddress> candidates, CancellationToken cancellation)
        {
            UnicastIPAddressInformation[] networks = PhysicalAddresses().ToArray();
            foreach (LanPeerAddress peer in candidates.OrderBy(p => PhysicalSubnet(p.Ip, networks) ? 0 : 1).Take(6))
            {
                cancellation.ThrowIfCancellationRequested();
                using (TcpClient tcp = new TcpClient())
                {
                    try
                    {
                        Task connect = tcp.ConnectAsync(peer.Ip, peer.Port);
                        connect.ContinueWith(t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        if (connect.Wait(600, cancellation) && tcp.Connected) return peer;
                    }
                    catch (AggregateException) { }
                    catch (SocketException) { }
                }
            }
            return null;
        }

        private void ValidateDestination(string ip)
        {
            if (!NetworkUtils.IsPrivateOrLinkLocal(ip)) throw new InvalidOperationException("名稱連線目前支援區域網路位址；跨網路請使用「網路連線」。");
            if (_isLocal(ip)) throw new InvalidOperationException("不能把本機 IP 設為遠端。");
        }

        internal static bool TryIPv4(string value, out string ip)
        {
            ip = ""; string[] parts = value.Split('.');
            if (parts.Length != 4) return false;
            byte[] bytes = new byte[4];
            for (int i = 0; i < 4; i++) if (!Regex.IsMatch(parts[i], @"^\d{1,3}$") || !byte.TryParse(parts[i], out bytes[i])) return false;
            ip = new IPAddress(bytes).ToString(); return true;
        }

        internal static bool ValidName(string value)
        {
            if (value.Length == 0 || value.Length > 253 || Regex.IsMatch(value, @"^[\d.]+$")) return false;
            return value.TrimEnd('.').Split('.').All(label => label.Length > 0 && label.Length <= 63 && Regex.IsMatch(label, @"^[\p{L}\p{N}_](?:[\p{L}\p{N}_-]*[\p{L}\p{N}_])?$"));
        }

        private static string[] ResolveDns(string host, CancellationToken cancellation)
        {
            Task<IPAddress[]> task = Task.Run(delegate { return Dns.GetHostAddresses(host); });
            task.ContinueWith(t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            try { return task.Wait(1200, cancellation) ? task.Result.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray() : new string[0]; }
            catch (AggregateException) { return new string[0]; }
        }

        private static IEnumerable<UnicastIPAddressInformation> PhysicalAddresses()
        {
            List<UnicastIPAddressInformation> result = new List<UnicastIPAddressInformation>();
            try
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up ||
                        (network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 && network.NetworkInterfaceType != NetworkInterfaceType.Ethernet)) continue;
                    try { result.AddRange(network.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask != null)); }
                    catch (NetworkInformationException) { }
                }
            }
            catch (NetworkInformationException) { }
            return result;
        }

        private IEnumerable<IPEndPoint> BroadcastTargets()
        {
            if (_discoveryPort == 0) yield break;
            foreach (UnicastIPAddressInformation address in PhysicalAddresses())
            {
                byte[] ip = address.Address.GetAddressBytes(), mask = address.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 255) || mask.All(b => b == 0)) continue;
                for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPEndPoint(new IPAddress(ip), _discoveryPort);
            }
        }

        private static bool PhysicalSubnet(string value, IEnumerable<UnicastIPAddressInformation> networks)
        {
            IPAddress remote;
            if (!IPAddress.TryParse(value, out remote) || remote.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] ip = remote.GetAddressBytes();
            return networks.Any(a => { byte[] own = a.Address.GetAddressBytes(), mask = a.IPv4Mask.GetAddressBytes(); return Enumerable.Range(0, 4).All(i => (own[i] & mask[i]) == (ip[i] & mask[i])); });
        }

        public void Dispose()
        {
            _running = false;
            if (_udp != null) _udp.Close();
            if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1000);
            lock (_sync) _peers.Clear();
        }
    }
}
