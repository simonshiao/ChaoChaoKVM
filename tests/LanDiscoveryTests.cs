extern alias Kvm;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;
using ChaoChaoKvm.Core;
using ChaoChaoKvm.Services;
using PeerServer = Kvm::ChaoChaoKvm.Services.HttpPeerServer;
using PeerClient = Kvm::ChaoChaoKvm.Services.HttpPeerClient;
using ScreenInfo = Kvm::ChaoChaoKvm.Services.ScreenInfo;

internal static class LanDiscoveryTests
{
    private static int _checks;
    private static IPAddress _bind;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; Console.WriteLine("PASS " + message); }
    private static void Wait(Func<bool> condition, string message)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 2500) Thread.Sleep(20);
        Check(condition(), message);
    }
    private static void Error(Action action, string text)
    {
        try { action(); } catch (InvalidOperationException ex) { Check(ex.Message.Contains(text), "reject " + text); return; }
        throw new Exception("Expected rejection: " + text);
    }
    private static LanDiscoveryService Node(string name, int port)
    {
        // The test seam permits same-machine peers; the public constructor rejects them.
        return new LanDiscoveryService(Guid.NewGuid().ToString("N"), name, port, 0, _bind, ip => false);
    }
    private static void Packet(int targetPort, object packet)
    {
        using (UdpClient sender = new UdpClient(new IPEndPoint(_bind, 0)))
        {
            byte[] data = JsonUtil.Utf8(packet);
            sender.Send(data, data.Length, new IPEndPoint(_bind, targetPort));
        }
    }

    public static int Main(string[] args)
    {
        _bind = args.Length > 0 ? IPAddress.Parse(args[0]) : IPAddress.Loopback;
        try
        {
            string ip;
            Check(LanDiscoveryService.TryIPv4("192.168.1.20", out ip) && ip == "192.168.1.20", "ordinary IP compatibility");
            foreach (string invalid in new[] { "127.1", "999.1.1.1", "0x7f.0.0.1", "192.168.1.1:8001" }) Check(!LanDiscoveryService.TryIPv4(invalid, out ip), "reject abbreviated or invalid IPv4 " + invalid);
            foreach (string valid in new[] { "Home-1", "TW.local", "workstation.example.local", "工作機" }) Check(LanDiscoveryService.ValidName(valid), "accept computer name " + valid);
            foreach (string invalid in new[] { "", "Home 1", "https://Home-1", "-Home", "Home-", "a..b", "999.1.1.1" }) Check(!LanDiscoveryService.ValidName(invalid), "reject invalid name " + invalid);

            using (PeerServer server = new PeerServer())
            {
                server.Start(0, _bind);
                server.SetLocalFrame(new byte[128], new ScreenInfo { Width = 100, Height = 100, Machine = "NAME-TEST" });
                using (LanDiscoveryService controller = Node("CONTROLLER", server.Port))
                using (LanDiscoveryService host = Node("TEST-TARGET", server.Port))
                {
                    controller.Start(); host.Start();
                    controller.SendQuery(new IPEndPoint(_bind, host.BoundPort));
                    Wait(() => controller.Peers().Any(p => p.Name == "TEST-TARGET"), "UDP query and reply discover the other KVM");
                    LanPeerAddress peer = controller.Resolve("test-target.local.", CancellationToken.None);
                    Check(peer.Ip == _bind.ToString() && peer.Port == server.Port, "case-insensitive name resolves to actual datagram source and HTTP port");
                    Check(!server.PeerAlive, "discovery and reachability probe do not trigger HTTP peer trust or liveness");
                    using (PeerClient client = new PeerClient())
                    {
                        int frames = 0;
                        client.RemoteFrame += data => { if (data.Length == 128) Interlocked.Increment(ref frames); };
                        client.Start(peer.Ip, peer.Port, false, false);
                        Wait(() => client.Connected && frames > 0, "resolved name connects through real KVM HTTP client and receives a frame");
                    }
                    using (LanDiscoveryService duplicate = Node("TEST-TARGET", server.Port))
                    {
                        duplicate.Start(); controller.SendQuery(new IPEndPoint(_bind, duplicate.BoundPort));
                        Wait(() => controller.Peers().Count(p => p.Name == "TEST-TARGET") == 2, "different device IDs preserve same-name collisions");
                        Error(() => controller.Resolve("TEST-TARGET", CancellationToken.None), "同名");
                    }
                    using (LanDiscoveryService differentPort = Node("OTHER-PORT", server.Port == 65535 ? 65534 : server.Port + 1))
                    {
                        differentPort.Start(); controller.SendQuery(new IPEndPoint(_bind, differentPort.BoundPort));
                        Wait(() => controller.Peers().Any(p => p.Name == "OTHER-PORT"), "discovery records an explicitly advertised port");
                        Error(() => controller.Resolve("OTHER-PORT", CancellationToken.None), "不同的連線埠");
                    }
                    int count = controller.Peers().Length;
                    Packet(controller.BoundPort, new { service = "OTHER_APP", kind = "reply", id = Guid.NewGuid().ToString("N"), name = "INVALID", port = server.Port });
                    Packet(controller.BoundPort, new { service = "CHAO_KVM_DISCOVERY_V1", kind = "reply", id = "not-an-id", name = "INVALID", port = server.Port });
                    Thread.Sleep(150);
                    Check(controller.Peers().Length == count, "malformed and unrelated discovery data are ignored");
                    Packet(controller.BoundPort, new { service = "CHAO_KVM_DISCOVERY_V1", kind = "reply", id = Guid.NewGuid().ToString("N"), name = "SOURCE-TEST", port = server.Port, ip = "8.8.8.8" });
                    Wait(() => controller.Peers().Any(p => p.Name == "SOURCE-TEST"), "valid location advertisement is received");
                    Check(controller.Peers().First(p => p.Name == "SOURCE-TEST").Ip == _bind.ToString(), "packet payload cannot redirect the source address");
                    foreach (LanPeerAddress expired in controller.Peers().Where(p => p.Name == "SOURCE-TEST")) expired.SeenUtc = DateTime.UtcNow.AddSeconds(-31);
                    Check(!controller.Peers().Any(p => p.Name == "SOURCE-TEST"), "offline location expires without a long sleep");
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        cancel.CancelAfter(80); Stopwatch watch = Stopwatch.StartNew(); bool canceled = false;
                        try { controller.Resolve("NO-SUCH-KVM", cancel.Token); } catch (OperationCanceledException) { canceled = true; }
                        Check(canceled && watch.ElapsedMilliseconds < 1000, "closing or replacing a lookup cancels promptly");
                    }
                    Stopwatch direct = Stopwatch.StartNew();
                    Check(controller.Resolve("192.168.99.123", CancellationToken.None).Ip == "192.168.99.123" && direct.ElapsedMilliseconds < 500, "manual IP preserves offline background retry behavior");
                    Error(() => controller.Resolve("8.8.8.8", CancellationToken.None), "區域網路");
                    Error(() => controller.Resolve("CONTROLLER", CancellationToken.None), "本機電腦名稱");
                }
            }
            using (PeerServer dnsHost = new PeerServer())
            {
                dnsHost.Start(0, IPAddress.Loopback);
                using (LanDiscoveryService dns = Node("DNS-CONTROLLER", dnsHost.Port))
                {
                    Check(dns.Resolve("localhost", CancellationToken.None).Ip == "127.0.0.1", "Windows name resolution works when UDP discovery is unavailable");
                    dns.Start();
                    Packet(dns.BoundPort, new { service = "CHAO_KVM_DISCOVERY_V1", kind = "reply", id = Guid.NewGuid().ToString("N"), name = "localhost", port = dnsHost.Port });
                    Wait(() => dns.Peers().Any(p => p.Name == "localhost"), "stale discovery fixture is received");
                    dns.Peers().First(p => p.Name == "localhost").Ip = "127.0.0.2";
                    Check(dns.Resolve("localhost", CancellationToken.None).Ip == "127.0.0.1", "stale discovery address falls back to Windows name resolution");
                }
            }
            using (LanDiscoveryService normal = new LanDiscoveryService(Guid.NewGuid().ToString("N"), "PRODUCTION", 8001))
            {
                Error(() => normal.Resolve("127.0.0.1", CancellationToken.None), "本機 IP");
                Error(() => normal.Resolve("127.0.0.2", CancellationToken.None), "本機 IP");
            }
            if (!IPAddress.IsLoopback(_bind))
            {
                int udpPort;
                using (UdpClient reservation = new UdpClient(new IPEndPoint(IPAddress.Any, 0))) udpPort = ((IPEndPoint)reservation.Client.LocalEndPoint).Port;
                var unicast = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).First(a => a.Address.Equals(_bind));
                byte[] destination = _bind.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++) destination[i] = (byte)(destination[i] | ~mask[i]);
                using (LanDiscoveryService host = new LanDiscoveryService(Guid.NewGuid().ToString("N"), "BROADCAST-HOST", 8001, udpPort, IPAddress.Any, ipValue => false))
                using (UdpClient query = new UdpClient(new IPEndPoint(_bind, 0)))
                {
                    host.Start(); query.EnableBroadcast = true; query.Client.ReceiveTimeout = 2000;
                    byte[] data = JsonUtil.Utf8(new { service = "CHAO_KVM_DISCOVERY_V1", kind = "query", id = Guid.NewGuid().ToString("N"), name = "WIFI-QUERY", port = 8001 });
                    query.Send(data, data.Length, new IPEndPoint(new IPAddress(destination), udpPort));
                    IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    var reply = JsonUtil.Parse(query.Receive(ref source));
                    Check(JsonUtil.GetString(reply, "name", "") == "BROADCAST-HOST" && source.Address.Equals(_bind), "Wi-Fi directed broadcast receives a KVM reply on this computer");
                }
            }
            Console.WriteLine("LAN_NAME_DISCOVERY_PASS " + _checks + " checks; bind=" + _bind + "; same-machine protocol test, no OS input/capture");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
