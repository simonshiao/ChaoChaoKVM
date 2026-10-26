using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ChaoChaoKvm.Core
{
    public sealed class AppConfig
    {
        public string ActivePeerIp { get; set; }
        public string LastPeerIp { get; set; }
        public bool ManualActivePeer { get; set; }
        public bool AllowRemoteControl { get; set; }
        public int Fps { get; set; }
        public int JpegQuality { get; set; }
        public string ReceiveDirectory { get; set; }
        public bool OverwriteFiles { get; set; }
        public int ListenPort { get; set; }
        public string OnlineAccessKey { get; set; }
        public string NgrokDomain { get; set; }
        public string LastOnlineEndpoint { get; set; }
        public string LastOnlinePeerKey { get; set; }
        public string OnlineDeviceId { get; set; }
        public string OnlineRegistrySecret { get; set; }
        public string OnlineRegistryUrl { get; set; }

        public static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BuildFlavor.IsOnline ? "ChaoChaoKvmOnline" : "ChaoChaoKvm");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "kvm_net472.json");
            }
        }

        public static AppConfig Load()
        {
            AppConfig config = new AppConfig
            {
                ActivePeerIp = "",
                LastPeerIp = "",
                ManualActivePeer = false,
                AllowRemoteControl = true,
                Fps = 10,
                JpegQuality = 55,
                ReceiveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", BuildFlavor.IsOnline ? "ChaoChaoKvmOnline" : "ChaoChaoKvm"),
                OverwriteFiles = false,
                ListenPort = BuildFlavor.IsOnline ? 18001 : Protocol.DefaultPort,
                OnlineAccessKey = BuildFlavor.IsOnline ? CreateAccessKey() : "",
                NgrokDomain = "",
                LastOnlineEndpoint = "",
                LastOnlinePeerKey = "",
                OnlineDeviceId = BuildFlavor.IsOnline ? "kvm_" + CreateAccessKey() : "",
                OnlineRegistrySecret = BuildFlavor.IsOnline ? CreateAccessKey() : "",
                OnlineRegistryUrl = BuildFlavor.IsOnline ? "https://fidel-multinucleolar-marvella.ngrok-free.dev" : ""
            };
            try
            {
                if (!File.Exists(ConfigPath)) return config;
                Dictionary<string, object> data = JsonUtil.Parse(File.ReadAllText(ConfigPath));
                config.ActivePeerIp = JsonUtil.GetString(data, "active_peer_ip", config.ActivePeerIp);
                config.LastPeerIp = JsonUtil.GetString(data, "last_peer_ip", config.LastPeerIp);
                config.ManualActivePeer = JsonUtil.GetBool(data, "manual_active_peer", config.ManualActivePeer);
                config.AllowRemoteControl = JsonUtil.GetBool(data, "allow_remote_control", config.AllowRemoteControl);
                config.Fps = Math.Max(2, Math.Min(30, JsonUtil.GetInt(data, "fps", config.Fps)));
                config.JpegQuality = Math.Max(25, Math.Min(85, JsonUtil.GetInt(data, "jpeg_quality", config.JpegQuality)));
                config.ReceiveDirectory = JsonUtil.GetString(data, "receive_directory", config.ReceiveDirectory);
                config.OverwriteFiles = JsonUtil.GetBool(data, "overwrite_files", config.OverwriteFiles);
                config.ListenPort = Math.Max(1, Math.Min(65535, JsonUtil.GetInt(data, "listen_port", config.ListenPort)));
                string protectedAccessKey = JsonUtil.GetString(data, "online_access_key_protected", "");
                string restoredAccessKey = UnprotectSecret(protectedAccessKey);
                config.OnlineAccessKey = string.IsNullOrWhiteSpace(restoredAccessKey) ? JsonUtil.GetString(data, "online_access_key", config.OnlineAccessKey) : restoredAccessKey;
                config.NgrokDomain = JsonUtil.GetString(data, "ngrok_domain", config.NgrokDomain);
                config.LastOnlineEndpoint = JsonUtil.GetString(data, "last_online_endpoint", config.LastOnlineEndpoint);
                config.LastOnlinePeerKey = JsonUtil.GetString(data, "last_online_peer_key", config.LastOnlinePeerKey);
                config.OnlineDeviceId = JsonUtil.GetString(data, "online_device_id", config.OnlineDeviceId);
                string protectedRegistrySecret = JsonUtil.GetString(data, "online_registry_secret_protected", "");
                string restoredRegistrySecret = UnprotectSecret(protectedRegistrySecret);
                if (!string.IsNullOrWhiteSpace(restoredRegistrySecret)) config.OnlineRegistrySecret = restoredRegistrySecret;
                config.OnlineRegistryUrl = JsonUtil.GetString(data, "online_registry_url", config.OnlineRegistryUrl);
                if (BuildFlavor.IsOnline && string.IsNullOrWhiteSpace(config.OnlineAccessKey)) config.OnlineAccessKey = CreateAccessKey();
                if (BuildFlavor.IsOnline && string.IsNullOrWhiteSpace(config.OnlineDeviceId)) config.OnlineDeviceId = "kvm_" + CreateAccessKey();
                if (BuildFlavor.IsOnline && string.IsNullOrWhiteSpace(config.OnlineRegistrySecret)) config.OnlineRegistrySecret = CreateAccessKey();
            }
            catch { }
            return config;
        }

        public void Save()
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["active_peer_ip"] = ActivePeerIp ?? "";
            data["last_peer_ip"] = LastPeerIp ?? "";
            data["manual_active_peer"] = ManualActivePeer;
            data["allow_remote_control"] = AllowRemoteControl;
            data["fps"] = Fps;
            data["jpeg_quality"] = JpegQuality;
            data["receive_directory"] = ReceiveDirectory ?? "";
            data["overwrite_files"] = OverwriteFiles;
            data["listen_port"] = ListenPort;
            data["online_access_key_protected"] = BuildFlavor.IsOnline ? ProtectSecret(OnlineAccessKey ?? "") : "";
            data["ngrok_domain"] = NgrokDomain ?? "";
            data["last_online_endpoint"] = LastOnlineEndpoint ?? "";
            data["online_device_id"] = OnlineDeviceId ?? "";
            data["online_registry_secret_protected"] = BuildFlavor.IsOnline ? ProtectSecret(OnlineRegistrySecret ?? "") : "";
            data["online_registry_url"] = OnlineRegistryUrl ?? "";
            File.WriteAllText(ConfigPath, JsonUtil.Serialize(data));
        }

        private static string CreateAccessKey()
        {
            byte[] bytes = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("ChaoChaoKvm.Online.Config.v1");

        private static string ProtectSecret(string secret)
        {
            byte[] raw = Encoding.UTF8.GetBytes(secret ?? "");
            return Convert.ToBase64String(ProtectedData.Protect(raw, SecretEntropy, DataProtectionScope.CurrentUser));
        }

        private static string UnprotectSecret(string protectedText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(protectedText)) return "";
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), SecretEntropy, DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }
    }
}
