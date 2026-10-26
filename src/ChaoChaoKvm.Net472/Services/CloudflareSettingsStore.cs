using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ChaoChaoKvm.Services
{
    public sealed class CloudflareSettings
    {
        public string TunnelToken { get; set; }
        public string Hostname { get; set; }
        public string RelayHostname { get; set; }
        public string RelayRoomId { get; set; }
        public string RelaySecret { get; set; }
    }

    public static class CloudflareSettingsStore
    {
        private static string PathName
        {
            get
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChaoChaoKvm");
                Directory.CreateDirectory(root);
                return Path.Combine(root, "cloudflare.json");
            }
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChaoChaoKvm.Cloudflare.Tunnel.v1");
        private static readonly byte[] RelayEntropy = Encoding.UTF8.GetBytes("ChaoChaoKvm.Cloudflare.Relay.v1");

        public static CloudflareSettings Load()
        {
            CloudflareSettings settings = new CloudflareSettings { TunnelToken = "", Hostname = "", RelayHostname = "kvm-relay.chaochao.win", RelayRoomId = NewHex(16), RelaySecret = NewSecret() };
            try
            {
                if (!File.Exists(PathName)) return settings;
                Dictionary<string, object> data = JsonUtil.Parse(File.ReadAllText(PathName));
                settings.Hostname = JsonUtil.GetString(data, "hostname", "").Trim();
                string protectedToken = JsonUtil.GetString(data, "tunnel_token_protected", "");
                if (!string.IsNullOrWhiteSpace(protectedToken))
                    settings.TunnelToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.CurrentUser));
                string relayHostname = JsonUtil.GetString(data, "relay_hostname", "").Trim();
                if (!string.IsNullOrWhiteSpace(relayHostname)) settings.RelayHostname = relayHostname;
                string relayRoom = JsonUtil.GetString(data, "relay_room_id", "").Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(relayRoom, "^[a-fA-F0-9]{32}$")) settings.RelayRoomId = relayRoom.ToLowerInvariant();
                string protectedSecret = JsonUtil.GetString(data, "relay_secret_protected", "");
                if (!string.IsNullOrWhiteSpace(protectedSecret))
                    settings.RelaySecret = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), RelayEntropy, DataProtectionScope.CurrentUser));
            }
            catch { settings.TunnelToken = ""; }
            return settings;
        }

        public static void Save(CloudflareSettings settings)
        {
            string protectedToken = "";
            if (!string.IsNullOrWhiteSpace(settings.TunnelToken))
                protectedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.TunnelToken.Trim()), Entropy, DataProtectionScope.CurrentUser));
            string protectedSecret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.RelaySecret ?? ""), RelayEntropy, DataProtectionScope.CurrentUser));
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["hostname"] = (settings.Hostname ?? "").Trim();
            data["tunnel_token_protected"] = protectedToken;
            data["relay_hostname"] = (settings.RelayHostname ?? "").Trim();
            data["relay_room_id"] = (settings.RelayRoomId ?? "").Trim();
            data["relay_secret_protected"] = protectedSecret;
            File.WriteAllText(PathName, JsonUtil.Serialize(data));
        }

        private static string NewHex(int count)
        {
            byte[] bytes = new byte[count];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            StringBuilder value = new StringBuilder(count * 2);
            foreach (byte item in bytes) value.Append(item.ToString("x2"));
            return value.ToString();
        }

        private static string NewSecret()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
