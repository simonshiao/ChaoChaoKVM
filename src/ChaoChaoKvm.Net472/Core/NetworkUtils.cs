using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace ChaoChaoKvm.Core
{
    public static class NetworkUtils
    {
        public static List<string> LocalIPv4()
        {
            List<string> result = new List<string>();
            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)) result.Add(address.ToString());
            }
            catch { }
            return result.Distinct().OrderBy(delegate(string ip) { return ip.StartsWith("169.254.", StringComparison.Ordinal) ? 0 : 1; }).ThenBy(x => x).ToList();
        }

        public static bool IsLocalAddress(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "127.0.0.1" || ip == "0.0.0.0") return true;
            return LocalIPv4().Any(x => string.Equals(x, ip, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsPrivateOrLinkLocal(string ip)
        {
            IPAddress address;
            if (!IPAddress.TryParse(ip, out address) || address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 127;
        }

        public static int ResolvePort()
        {
            int port;
            string raw = Environment.GetEnvironmentVariable("CHAO_KVM_PORT");
            return int.TryParse(raw, out port) && port > 1024 && port <= 65535 ? port : Protocol.DefaultPort;
        }

        public static string HumanSize(long bytes)
        {
            double value = Math.Max(0, bytes);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int index = 0;
            while (value >= 1024.0 && index < units.Length - 1)
            {
                value /= 1024.0;
                index++;
            }
            return value.ToString("0.0") + " " + units[index];
        }
    }
}
