using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ChaoChaoKvm.Services
{
    // Small DNS-SD advertiser/browser for the KVM service. It carries only public
    // endpoint metadata; peer trust is still decided by the existing HTTP protocol.
    internal sealed class MdnsLanDiscovery : IDisposable
    {
        private const int MdnsPort = 5353;
        private const ushort DnsTypeA = 1;
        private const ushort DnsTypePtr = 12;
        private const ushort DnsTypeTxt = 16;
        private const ushort DnsTypeSrv = 33;
        private const ushort DnsTypeAny = 255;
        private const ushort DnsClassIn = 1;
        private const string ServiceType = "_chaokvm._tcp.local.";
        private const string InstanceSuffix = "._chaokvm._tcp.local.";
        private static readonly IPAddress GroupAddress = IPAddress.Parse("224.0.0.251");
        private static readonly IPEndPoint GroupEndpoint = new IPEndPoint(GroupAddress, MdnsPort);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly string _id;
        private readonly string _name;
        private readonly int _port;
        private readonly Func<string, bool> _isLocal;
        private readonly Action<LanPeerAddress> _onPeer;
        private readonly object _socketSync = new object();
        private readonly object _cacheSync = new object();
        private readonly HashSet<IPAddress> _joinedInterfaces = new HashSet<IPAddress>();
        private readonly Dictionary<string, MdnsPeerState> _services = new Dictionary<string, MdnsPeerState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, DateTime>> _addresses = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
        private Socket _socket;
        private Thread _thread;
        private volatile bool _running;

        internal MdnsLanDiscovery(string id, string name, int port, Func<string, bool> isLocal, Action<LanPeerAddress> onPeer)
        {
            _id = id ?? "";
            _name = name ?? "";
            _port = port;
            _isLocal = isLocal;
            _onPeer = onPeer;
        }

        internal bool Running { get { return _running; } }

        internal void Start()
        {
            if (_running) return;
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                socket.ReceiveTimeout = 1000;
                socket.ReceiveBufferSize = 64 * 1024;
                _socket = socket;
                _running = true;
                RefreshInterfaces();
                _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "ChaoKvmMdnsDiscovery" };
                _thread.Start();
            }
            catch
            {
                _running = false;
                socket.Close();
                _socket = null;
                throw;
            }
        }

        internal void Search()
        {
            if (!_running) return;
            RefreshInterfaces();
            SendMulticast(BuildQuery());
        }

        private void ReceiveLoop()
        {
            DateTime refreshAt = DateTime.MinValue;
            DateTime announceAt = DateTime.MinValue;
            byte[] buffer = new byte[9000];
            while (_running)
            {
                DateTime now = DateTime.UtcNow;
                if (now >= refreshAt)
                {
                    RefreshInterfaces();
                    refreshAt = now.AddSeconds(5);
                }
                if (now >= announceAt)
                {
                    SendMulticast(BuildAnnouncement());
                    announceAt = now.AddSeconds(20);
                }

                try
                {
                    EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    int count = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref source);
                    IPEndPoint endpoint = source as IPEndPoint;
                    if (endpoint != null && count >= 12 && count <= buffer.Length)
                    {
                        byte[] packet = new byte[count];
                        Buffer.BlockCopy(buffer, 0, packet, 0, count);
                        ProcessPacket(packet, endpoint);
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.TimedOut && _running)
                        System.Diagnostics.Trace.TraceWarning("KVM mDNS receive: " + ex.Message);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_running) System.Diagnostics.Trace.TraceWarning("KVM mDNS packet ignored: " + ex.Message);
                }
            }
        }

        private void RefreshInterfaces()
        {
            Socket socket = _socket;
            if (!_running || socket == null) return;
            IPAddress[] desired = PhysicalIPv4Addresses();
            lock (_socketSync)
            {
                if (!_running || !ReferenceEquals(socket, _socket)) return;
                foreach (IPAddress address in desired)
                {
                    if (_joinedInterfaces.Contains(address)) continue;
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GroupAddress, address));
                        _joinedInterfaces.Add(address);
                    }
                    catch (SocketException ex) { System.Diagnostics.Trace.TraceWarning("KVM mDNS join " + address + ": " + ex.Message); }
                    catch (ObjectDisposedException) { return; }
                }
                foreach (IPAddress address in _joinedInterfaces.Where(a => !desired.Contains(a)).ToArray())
                {
                    try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(GroupAddress, address)); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { return; }
                    _joinedInterfaces.Remove(address);
                }
            }
        }

        private static IPAddress[] PhysicalIPv4Addresses()
        {
            List<IPAddress> addresses = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up ||
                        (network.NetworkInterfaceType != NetworkInterfaceType.Ethernet && network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)) continue;
                    try
                    {
                        foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                        {
                            if (address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask != null &&
                                NetworkUtils.IsPrivateOrLinkLocal(address.Address.ToString())) addresses.Add(address.Address);
                        }
                    }
                    catch (NetworkInformationException) { }
                }
            }
            catch (NetworkInformationException) { }
            return addresses.Distinct().ToArray();
        }

        private void SendMulticast(byte[] packet)
        {
            Socket socket = _socket;
            if (!_running || socket == null || packet == null) return;
            foreach (IPAddress address in PhysicalIPv4Addresses())
            {
                lock (_socketSync)
                {
                    if (!_running || !ReferenceEquals(socket, _socket)) return;
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        socket.SendTo(packet, 0, packet.Length, SocketFlags.None, GroupEndpoint);
                    }
                    catch (SocketException ex) { System.Diagnostics.Trace.TraceWarning("KVM mDNS send on " + address + ": " + ex.Message); }
                    catch (ObjectDisposedException) { return; }
                }
            }
        }

        private void SendUnicast(byte[] packet, IPEndPoint destination)
        {
            Socket socket = _socket;
            if (!_running || socket == null || packet == null) return;
            lock (_socketSync)
            {
                if (!_running || !ReferenceEquals(socket, _socket)) return;
                try { socket.SendTo(packet, 0, packet.Length, SocketFlags.None, destination); }
                catch (SocketException ex) { System.Diagnostics.Trace.TraceWarning("KVM mDNS reply: " + ex.Message); }
                catch (ObjectDisposedException) { }
            }
        }

        private void ProcessPacket(byte[] packet, IPEndPoint source)
        {
            if (!NetworkUtils.IsPrivateOrLinkLocal(source.Address.ToString())) return;
            int flags = ReadUInt16(packet, 2);
            int questionCount = ReadUInt16(packet, 4);
            int recordCount = ReadUInt16(packet, 6) + ReadUInt16(packet, 8) + ReadUInt16(packet, 10);
            if (questionCount > 64 || recordCount > 256) return;

            int offset = 12;
            List<DnsQuestion> questions = new List<DnsQuestion>();
            for (int i = 0; i < questionCount; i++)
            {
                string name = ReadDomainName(packet, ref offset, packet.Length);
                EnsureAvailable(packet, offset, 4, packet.Length);
                questions.Add(new DnsQuestion { Name = name, Type = ReadUInt16(packet, offset), Class = ReadUInt16(packet, offset + 2) });
                offset += 4;
            }

            List<DnsRecord> records = new List<DnsRecord>();
            int totalRecords = ReadUInt16(packet, 6) + ReadUInt16(packet, 8) + ReadUInt16(packet, 10);
            for (int i = 0; i < totalRecords; i++)
            {
                string name = ReadDomainName(packet, ref offset, packet.Length);
                EnsureAvailable(packet, offset, 10, packet.Length);
                ushort type = ReadUInt16(packet, offset);
                ushort recordClass = ReadUInt16(packet, offset + 2);
                uint ttl = ReadUInt32(packet, offset + 4);
                int dataLength = ReadUInt16(packet, offset + 8);
                offset += 10;
                EnsureAvailable(packet, offset, dataLength, packet.Length);
                records.Add(new DnsRecord { Name = name, Type = type, Class = recordClass, Ttl = ttl, DataOffset = offset, DataLength = dataLength, Packet = packet });
                offset += dataLength;
            }

            if ((flags & 0x8000) == 0)
            {
                bool requested = questions.Any(q => SameName(q.Name, ServiceType) &&
                    ((q.Type == DnsTypePtr || q.Type == DnsTypeAny) && (q.Class & 0x7fff) == DnsClassIn));
                if (!requested) return;
                byte[] answer = BuildAnnouncement();
                bool unicast = source.Port == MdnsPort && questions.Any(q => SameName(q.Name, ServiceType) && (q.Class & 0x8000) != 0);
                if (unicast) SendUnicast(answer, source); else SendMulticast(answer);
                return;
            }

            AcceptRecords(records);
        }

        private void AcceptRecords(List<DnsRecord> records)
        {
            DateTime now = DateTime.UtcNow;
            List<LanPeerAddress> found = new List<LanPeerAddress>();
            lock (_cacheSync)
            {
                ExpireCache(now);
                foreach (DnsRecord record in records.Where(r => (r.Class & 0x7fff) == DnsClassIn && r.Type == DnsTypePtr && SameName(r.Name, ServiceType)))
                {
                    int cursor = record.DataOffset;
                    string instance = ReadDomainName(record.Packet, ref cursor, record.DataOffset + record.DataLength);
                    if (!instance.EndsWith(InstanceSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    MdnsPeerState state = GetState(instance);
                    if (record.Ttl == 0) { _services.Remove(instance); continue; }
                    state.LastSeenUtc = now;
                }

                foreach (DnsRecord record in records.Where(r => (r.Class & 0x7fff) == DnsClassIn))
                {
                    if (record.Type == DnsTypeSrv && record.DataLength >= 7 && record.Name.EndsWith(InstanceSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        MdnsPeerState state = GetState(record.Name);
                        if (record.Ttl == 0) { state.Port = 0; continue; }
                        int cursor = record.DataOffset;
                        EnsureAvailable(record.Packet, cursor, 6, record.DataOffset + record.DataLength);
                        state.Port = ReadUInt16(record.Packet, cursor + 4);
                        cursor += 6;
                        state.Host = ReadDomainName(record.Packet, ref cursor, record.DataOffset + record.DataLength);
                        state.LastSeenUtc = now;
                    }
                    else if (record.Type == DnsTypeTxt && record.Name.EndsWith(InstanceSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        MdnsPeerState state = GetState(record.Name);
                        if (record.Ttl == 0) continue;
                        Dictionary<string, string> values = ReadTextValues(record.Packet, record.DataOffset, record.DataLength);
                        string value;
                        if (values.TryGetValue("id", out value)) state.Id = value;
                        if (values.TryGetValue("name", out value)) state.Name = value;
                        if (values.TryGetValue("v", out value)) state.Version = value;
                        state.LastSeenUtc = now;
                    }
                    else if (record.Type == DnsTypeA && record.DataLength == 4 && record.Name.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
                    {
                        IPAddress address = new IPAddress(new byte[] { record.Packet[record.DataOffset], record.Packet[record.DataOffset + 1], record.Packet[record.DataOffset + 2], record.Packet[record.DataOffset + 3] });
                        string ip = address.ToString();
                        if (record.Ttl == 0 || !NetworkUtils.IsPrivateOrLinkLocal(ip) || _isLocal(ip)) continue;
                        Dictionary<string, DateTime> values;
                        if (!_addresses.TryGetValue(record.Name, out values))
                        {
                            if (_addresses.Count >= 128) _addresses.Clear();
                            _addresses[record.Name] = values = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                        }
                        values[ip] = now.AddSeconds(Math.Max(15, Math.Min(120, record.Ttl)));
                    }
                }

                foreach (MdnsPeerState state in _services.Values)
                {
                    Guid parsed;
                    if (state.LastSeenUtc == DateTime.MinValue || now - state.LastSeenUtc > TimeSpan.FromSeconds(75) ||
                        !Guid.TryParseExact(state.Id, "N", out parsed) || state.Version != "1" ||
                        state.Port < 1025 || state.Port > 65535 || !ValidDisplayName(state.Name) || string.IsNullOrEmpty(state.Host)) continue;
                    Dictionary<string, DateTime> addresses;
                    if (!_addresses.TryGetValue(state.Host, out addresses)) continue;
                    foreach (KeyValuePair<string, DateTime> address in addresses.Where(a => a.Value > now))
                    {
                        found.Add(new LanPeerAddress { Id = parsed.ToString("N"), Name = state.Name, Ip = address.Key, Port = state.Port, SeenUtc = now });
                    }
                }
            }

            foreach (LanPeerAddress peer in found)
            {
                try { _onPeer(peer); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("KVM mDNS peer callback: " + ex.Message); }
            }
        }

        private void ExpireCache(DateTime now)
        {
            foreach (string key in _services.Where(p => p.Value.LastSeenUtc != DateTime.MinValue && now - p.Value.LastSeenUtc > TimeSpan.FromSeconds(75)).Select(p => p.Key).ToArray())
                _services.Remove(key);
            foreach (string host in _addresses.Keys.ToArray())
            {
                Dictionary<string, DateTime> addresses = _addresses[host];
                foreach (string ip in addresses.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) addresses.Remove(ip);
                if (addresses.Count == 0) _addresses.Remove(host);
            }
        }

        private MdnsPeerState GetState(string instance)
        {
            MdnsPeerState state;
            if (!_services.TryGetValue(instance, out state))
            {
                if (_services.Count >= 128) _services.Clear();
                _services[instance] = state = new MdnsPeerState();
            }
            return state;
        }

        private byte[] BuildAnnouncement()
        {
            Guid id;
            if (!Guid.TryParseExact(_id, "N", out id) || _port < 1025 || _port > 65535) return null;
            string name = LimitUtf8(_name.Trim(), 220);
            if (!ValidDisplayName(name)) return null;
            IPAddress[] addresses = PhysicalIPv4Addresses();
            if (addresses.Length == 0) return null;

            string canonicalId = id.ToString("N");
            string instance = canonicalId + InstanceSuffix;
            string host = canonicalId + ".local.";
            List<byte> packet = NewHeader(0x8400, 0, 3 + addresses.Length);

            List<byte> ptr = new List<byte>();
            WriteDomainName(ptr, instance);
            AddRecord(packet, ServiceType, DnsTypePtr, DnsClassIn, 120, ptr.ToArray());

            List<byte> srv = new List<byte>();
            WriteUInt16(srv, 0);
            WriteUInt16(srv, 0);
            WriteUInt16(srv, (ushort)_port);
            WriteDomainName(srv, host);
            AddRecord(packet, instance, DnsTypeSrv, (ushort)(0x8000 | DnsClassIn), 120, srv.ToArray());

            List<byte> txt = new List<byte>();
            AddTextValue(txt, "id=" + canonicalId);
            AddTextValue(txt, "name=" + name);
            AddTextValue(txt, "v=1");
            AddRecord(packet, instance, DnsTypeTxt, (ushort)(0x8000 | DnsClassIn), 120, txt.ToArray());

            foreach (IPAddress address in addresses)
                AddRecord(packet, host, DnsTypeA, (ushort)(0x8000 | DnsClassIn), 120, address.GetAddressBytes());
            return packet.ToArray();
        }

        private static byte[] BuildQuery()
        {
            List<byte> packet = NewHeader(0, 1, 0);
            WriteDomainName(packet, ServiceType);
            WriteUInt16(packet, DnsTypePtr);
            WriteUInt16(packet, (ushort)(0x8000 | DnsClassIn)); // QU: request a direct reply to this mDNS socket.
            return packet.ToArray();
        }

        private static List<byte> NewHeader(ushort flags, int questionCount, int answerCount)
        {
            List<byte> packet = new List<byte>(512);
            WriteUInt16(packet, 0);
            WriteUInt16(packet, flags);
            WriteUInt16(packet, (ushort)questionCount);
            WriteUInt16(packet, (ushort)answerCount);
            WriteUInt16(packet, 0);
            WriteUInt16(packet, 0);
            return packet;
        }

        private static void AddRecord(List<byte> packet, string name, ushort type, ushort recordClass, uint ttl, byte[] data)
        {
            WriteDomainName(packet, name);
            WriteUInt16(packet, type);
            WriteUInt16(packet, recordClass);
            WriteUInt32(packet, ttl);
            WriteUInt16(packet, (ushort)data.Length);
            packet.AddRange(data);
        }

        private static void AddTextValue(List<byte> data, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 255) throw new InvalidOperationException("mDNS TXT value exceeds 255 bytes.");
            data.Add((byte)bytes.Length);
            data.AddRange(bytes);
        }

        private static void WriteDomainName(List<byte> data, string name)
        {
            string[] labels = name.TrimEnd('.').Split('.');
            foreach (string label in labels)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length == 0 || bytes.Length > 63) throw new InvalidOperationException("Invalid mDNS DNS label.");
                data.Add((byte)bytes.Length);
                data.AddRange(bytes);
            }
            data.Add(0);
        }

        private static string ReadDomainName(byte[] packet, ref int offset, int fieldEnd)
        {
            int cursor = offset;
            int resume = -1;
            int pointerCount = 0;
            HashSet<int> visited = new HashSet<int>();
            List<string> labels = new List<string>();
            while (true)
            {
                int limit = resume < 0 ? fieldEnd : packet.Length;
                if (cursor >= limit) throw new FormatException("Truncated DNS name.");
                byte length = packet[cursor++];
                if ((length & 0xc0) == 0xc0)
                {
                    if (cursor >= limit) throw new FormatException("Truncated DNS compression pointer.");
                    int target = ((length & 0x3f) << 8) | packet[cursor++];
                    if (target >= packet.Length || !visited.Add(target) || ++pointerCount > 16) throw new FormatException("Invalid DNS compression pointer.");
                    if (resume < 0) resume = cursor;
                    cursor = target;
                    continue;
                }
                if ((length & 0xc0) != 0 || length > 63) throw new FormatException("Invalid DNS label.");
                if (length == 0)
                {
                    offset = resume < 0 ? cursor : resume;
                    return labels.Count == 0 ? "." : string.Join(".", labels) + ".";
                }
                if (cursor + length > limit) throw new FormatException("Truncated DNS label.");
                labels.Add(StrictUtf8.GetString(packet, cursor, length));
                if (labels.Count > 127) throw new FormatException("DNS name has too many labels.");
                cursor += length;
            }
        }

        private static Dictionary<string, string> ReadTextValues(byte[] packet, int offset, int length)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int end = offset + length;
            EnsureAvailable(packet, offset, length, packet.Length);
            while (offset < end)
            {
                int valueLength = packet[offset++];
                EnsureAvailable(packet, offset, valueLength, end);
                string value = StrictUtf8.GetString(packet, offset, valueLength);
                offset += valueLength;
                int separator = value.IndexOf('=');
                if (separator > 0) values[value.Substring(0, separator)] = value.Substring(separator + 1);
            }
            return values;
        }

        private static bool ValidDisplayName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl);
        }

        private static string LimitUtf8(string value, int maxBytes)
        {
            StringBuilder result = new StringBuilder();
            int byteCount = 0;
            for (int i = 0; i < value.Length; i++)
            {
                string character = value[i].ToString();
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    character += value[++i];
                int length = Encoding.UTF8.GetByteCount(character);
                if (byteCount + length > maxBytes) break;
                result.Append(character);
                byteCount += length;
            }
            return result.ToString();
        }

        private static bool SameName(string left, string right)
        {
            return string.Equals((left ?? "").TrimEnd('.'), (right ?? "").TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            EnsureAvailable(data, offset, 2, data.Length);
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            EnsureAvailable(data, offset, 4, data.Length);
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static void WriteUInt16(List<byte> data, ushort value)
        {
            data.Add((byte)(value >> 8));
            data.Add((byte)value);
        }

        private static void WriteUInt32(List<byte> data, uint value)
        {
            data.Add((byte)(value >> 24));
            data.Add((byte)(value >> 16));
            data.Add((byte)(value >> 8));
            data.Add((byte)value);
        }

        private static void EnsureAvailable(byte[] data, int offset, int count, int limit)
        {
            if (offset < 0 || count < 0 || limit < 0 || limit > data.Length || offset > limit - count)
                throw new FormatException("Truncated DNS packet.");
        }

        public void Dispose()
        {
            _running = false;
            Socket socket = _socket;
            lock (_socketSync)
            {
                _socket = null;
                if (socket != null)
                {
                    try { socket.Close(); }
                    catch (SocketException) { }
                }
                _joinedInterfaces.Clear();
            }
            if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1200);
            lock (_cacheSync) { _services.Clear(); _addresses.Clear(); }
        }

        private sealed class DnsQuestion
        {
            internal string Name;
            internal ushort Type;
            internal ushort Class;
        }

        private sealed class DnsRecord
        {
            internal string Name;
            internal ushort Type;
            internal ushort Class;
            internal uint Ttl;
            internal byte[] Packet;
            internal int DataOffset;
            internal int DataLength;
        }

        private sealed class MdnsPeerState
        {
            internal string Id = "";
            internal string Name = "";
            internal string Version = "";
            internal string Host = "";
            internal int Port;
            internal DateTime LastSeenUtc = DateTime.MinValue;
        }
    }
}
