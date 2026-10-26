using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ChaoChaoKvm.Core
{
    public sealed class DeviceProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Ip { get; set; }
        public string Group { get; set; }
        public List<string> Tags { get; set; }
        public bool Favorite { get; set; }
        public string LastSeenUtc { get; set; }
        public string ConnectionMode { get; set; }
        public string OnlineEndpoint { get; set; }
        public string ProtectedOnlineKey { get; set; }
        public string RegistryUrl { get; set; }
        public string RegistryDeviceId { get; set; }
        public string ProtectedRegistrySecret { get; set; }

        public DeviceProfile()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "遠端電腦";
            Ip = "";
            Group = "未分類";
            Tags = new List<string>();
            LastSeenUtc = "";
            ConnectionMode = "lan";
            OnlineEndpoint = "";
            ProtectedOnlineKey = "";
            RegistryUrl = "";
            RegistryDeviceId = "";
            ProtectedRegistrySecret = "";
        }
    }

    public sealed class OnlineDeviceCredential
    {
        public string DeviceId { get; set; }
        public string Name { get; set; }
        public string Endpoint { get; set; }
        public string AccessKey { get; set; }
        public string RegistryUrl { get; set; }
        public string RegistryDeviceId { get; set; }
        public string RegistrySecret { get; set; }
    }

    public sealed class DeviceLibraryStore
    {
        private readonly object _sync = new object();
        private readonly string _path;
        private readonly List<string> _groups = new List<string>();
        private readonly List<DeviceProfile> _devices = new List<DeviceProfile>();

        public DeviceLibraryStore() : this(DefaultPath) { }

        public DeviceLibraryStore(string path)
        {
            _path = path;
            Load();
        }

        public static string DefaultPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BuildFlavor.IsOnline ? "ChaoChaoKvmOnline" : "ChaoChaoKvm");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "devices.json");
            }
        }

        public List<string> Groups
        {
            get { lock (_sync) return new List<string>(_groups); }
        }

        public List<DeviceProfile> Devices
        {
            get
            {
                lock (_sync)
                {
                    return _devices.Select(Clone).ToList();
                }
            }
        }

        public DeviceProfile FindByIp(string ip)
        {
            lock (_sync)
            {
                DeviceProfile device = _devices.FirstOrDefault(d => string.Equals(d.Ip, ip, StringComparison.OrdinalIgnoreCase));
                return device == null ? null : Clone(device);
            }
        }

        public DeviceProfile FindById(string id)
        {
            lock (_sync)
            {
                DeviceProfile device = _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                return device == null ? null : Clone(device);
            }
        }

        public OnlineDeviceCredential GetOnlineCredential(string id)
        {
            lock (_sync)
            {
                DeviceProfile device = _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (device == null || !string.Equals(device.ConnectionMode, "online", StringComparison.OrdinalIgnoreCase)) return null;
                string key = UnprotectSecret(device.ProtectedOnlineKey);
                if (string.IsNullOrWhiteSpace(key)) return null;
                return new OnlineDeviceCredential
                {
                    DeviceId = device.Id,
                    Name = device.Name,
                    Endpoint = device.OnlineEndpoint,
                    AccessKey = key,
                    RegistryUrl = device.RegistryUrl,
                    RegistryDeviceId = device.RegistryDeviceId,
                    RegistrySecret = UnprotectSecret(device.ProtectedRegistrySecret)
                };
            }
        }

        public DeviceProfile Upsert(string id, string name, string ip, string group, string tagsCsv, bool favorite)
        {
            ip = (ip ?? "").Trim();
            name = (name ?? "").Trim();
            group = NormalizeGroup(group);
            if (ip.Length == 0 || !NetworkUtils.IsPrivateOrLinkLocal(ip)) throw new InvalidOperationException("裝置 IP 必須是 LAN / 169.254.x.x 私有位址。");
            if (NetworkUtils.IsLocalAddress(ip)) throw new InvalidOperationException("不能把本機 IP 加入遠端裝置。");
            if (name.Length == 0) name = "遠端電腦";

            lock (_sync)
            {
                DeviceProfile device = null;
                if (!string.IsNullOrWhiteSpace(id)) device = _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (device == null) device = _devices.FirstOrDefault(d => string.Equals(d.Ip, ip, StringComparison.OrdinalIgnoreCase));
                if (device == null)
                {
                    device = new DeviceProfile();
                    _devices.Add(device);
                }

                device.Name = name;
                device.Ip = ip;
                device.ConnectionMode = "lan";
                device.OnlineEndpoint = "";
                device.ProtectedOnlineKey = "";
                device.RegistryUrl = "";
                device.RegistryDeviceId = "";
                device.ProtectedRegistrySecret = "";
                device.Group = group;
                device.Tags = ParseTags(tagsCsv);
                device.Favorite = favorite;
                EnsureGroup(group);
                SaveLocked();
                return Clone(device);
            }
        }

        public DeviceProfile UpsertOnline(string id, string name, string endpoint, string accessKey, string group, string tagsCsv, bool favorite)
        {
            return UpsertOnline(id, name, endpoint, accessKey, group, tagsCsv, favorite, "", "", "");
        }

        public DeviceProfile UpsertOnline(string id, string name, string endpoint, string accessKey, string group, string tagsCsv, bool favorite, string registryUrl, string registryDeviceId, string registrySecret)
        {
            endpoint = NormalizeOnlineEndpoint(endpoint);
            accessKey = (accessKey ?? "").Trim();
            name = (name ?? "").Trim();
            group = NormalizeGroup(group);
            registryUrl = NormalizeRegistryUrl(registryUrl);
            registryDeviceId = (registryDeviceId ?? "").Trim();
            registrySecret = (registrySecret ?? "").Trim();
            if (endpoint.Length == 0) throw new InvalidOperationException("Online endpoint 格式不正確。");
            if (accessKey.Length < 16) throw new InvalidOperationException("Online access key 格式不正確。");
            if ((registryUrl.Length > 0 || registryDeviceId.Length > 0 || registrySecret.Length > 0) && (registryUrl.Length == 0 || registryDeviceId.Length < 16 || registrySecret.Length < 24))
                throw new InvalidOperationException("Online registry 配對資料格式不正確。");
            if (name.Length == 0) name = "Online 遠端";

            lock (_sync)
            {
                DeviceProfile device = null;
                if (!string.IsNullOrWhiteSpace(id)) device = _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (device == null && registryDeviceId.Length > 0)
                    device = _devices.FirstOrDefault(d => string.Equals(d.ConnectionMode, "online", StringComparison.OrdinalIgnoreCase) && string.Equals(d.RegistryDeviceId, registryDeviceId, StringComparison.OrdinalIgnoreCase));
                if (device == null && registryDeviceId.Length > 0)
                    device = _devices.FirstOrDefault(d => string.Equals(d.ConnectionMode, "online", StringComparison.OrdinalIgnoreCase) && string.Equals(UnprotectSecret(d.ProtectedOnlineKey), accessKey, StringComparison.Ordinal));
                if (device == null)
                    device = _devices.FirstOrDefault(d => string.Equals(d.ConnectionMode, "online", StringComparison.OrdinalIgnoreCase) && string.Equals(d.OnlineEndpoint, endpoint, StringComparison.OrdinalIgnoreCase));
                if (device == null)
                {
                    device = new DeviceProfile();
                    _devices.Add(device);
                }

                device.Name = name;
                device.Ip = "";
                device.ConnectionMode = "online";
                device.OnlineEndpoint = endpoint;
                device.ProtectedOnlineKey = ProtectSecret(accessKey);
                device.RegistryUrl = registryUrl;
                device.RegistryDeviceId = registryDeviceId;
                device.ProtectedRegistrySecret = registrySecret.Length == 0 ? "" : ProtectSecret(registrySecret);
                device.Group = group;
                device.Tags = ParseTags(tagsCsv);
                device.Favorite = favorite;
                device.LastSeenUtc = DateTime.UtcNow.ToString("o");
                EnsureGroup(group);
                SaveLocked();
                return Clone(device);
            }
        }

        public DeviceProfile RememberConnection(string ip, string machineName)
        {
            ip = (ip ?? "").Trim();
            if (ip.Length == 0 || !NetworkUtils.IsPrivateOrLinkLocal(ip) || NetworkUtils.IsLocalAddress(ip)) return null;
            lock (_sync)
            {
                DeviceProfile device = _devices.FirstOrDefault(d => string.Equals(d.Ip, ip, StringComparison.OrdinalIgnoreCase));
                if (device == null)
                {
                    device = new DeviceProfile { Ip = ip, Name = string.IsNullOrWhiteSpace(machineName) ? "遠端電腦" : machineName.Trim(), Group = "未分類" };
                    _devices.Add(device);
                }
                else if (!string.IsNullOrWhiteSpace(machineName) && (device.Name == "遠端電腦" || device.Name == device.Ip))
                {
                    device.Name = machineName.Trim();
                }
                device.ConnectionMode = "lan";
                device.OnlineEndpoint = "";
                device.ProtectedOnlineKey = "";
                device.RegistryUrl = "";
                device.RegistryDeviceId = "";
                device.ProtectedRegistrySecret = "";
                device.LastSeenUtc = DateTime.UtcNow.ToString("o");
                SaveLocked();
                return Clone(device);
            }
        }

        public void AddGroup(string name)
        {
            name = NormalizeGroup(name);
            lock (_sync)
            {
                EnsureGroup(name);
                SaveLocked();
            }
        }

        public void RemoveGroup(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || string.Equals(name, "未分類", StringComparison.OrdinalIgnoreCase)) return;
            lock (_sync)
            {
                _groups.RemoveAll(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));
                foreach (DeviceProfile device in _devices)
                    if (string.Equals(device.Group, name, StringComparison.OrdinalIgnoreCase)) device.Group = "未分類";
                EnsureGroup("未分類");
                SaveLocked();
            }
        }

        public void RemoveDevice(string id)
        {
            lock (_sync)
            {
                _devices.RemoveAll(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                SaveLocked();
            }
        }

        public void ToggleFavorite(string id)
        {
            lock (_sync)
            {
                DeviceProfile device = _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (device == null) return;
                device.Favorite = !device.Favorite;
                SaveLocked();
            }
        }

        public DeviceProfile UpdateMetadata(string id, string name, string group, string tagsCsv, bool favorite)
        {
            name = (name ?? "").Trim();
            group = NormalizeGroup(group);
            lock (_sync)
            {
                DeviceProfile device = _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (device == null) throw new InvalidOperationException("找不到要更新的裝置。");
                if (name.Length > 0) device.Name = name;
                device.Group = group;
                device.Tags = ParseTags(tagsCsv);
                device.Favorite = favorite;
                EnsureGroup(group);
                SaveLocked();
                return Clone(device);
            }
        }

        public void UpdateOnlineEndpoint(string id, string endpoint, string name)
        {
            endpoint = NormalizeOnlineEndpoint(endpoint);
            if (endpoint.Length == 0) return;
            lock (_sync)
            {
                DeviceProfile device = _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (device == null || !string.Equals(device.ConnectionMode, "online", StringComparison.OrdinalIgnoreCase)) return;
                string oldEndpoint = device.OnlineEndpoint;
                device.OnlineEndpoint = endpoint;
                if (!string.IsNullOrWhiteSpace(name) && (device.Name == "Online 遠端" || device.Name == oldEndpoint)) device.Name = name.Trim();
                device.LastSeenUtc = DateTime.UtcNow.ToString("o");
                SaveLocked();
            }
        }

        public object Snapshot()
        {
            lock (_sync)
            {
                return new
                {
                    groups = _groups.ToArray(),
                    devices = _devices.Select(d => new
                    {
                        id = d.Id,
                        name = d.Name,
                        ip = d.Ip,
                        mode = d.ConnectionMode,
                        endpoint = d.OnlineEndpoint,
                        registry = !string.IsNullOrWhiteSpace(d.RegistryUrl) && !string.IsNullOrWhiteSpace(d.RegistryDeviceId),
                        group = d.Group,
                        tags = d.Tags.ToArray(),
                        favorite = d.Favorite,
                        lastSeenUtc = d.LastSeenUtc
                    }).ToArray()
                };
            }
        }

        private void Load()
        {
            lock (_sync)
            {
                _groups.Clear();
                _devices.Clear();
                EnsureGroup("未分類");
                EnsureGroup("家用");
                EnsureGroup("公司");
                EnsureGroup("AOI 機台");
                EnsureGroup("AI 訓練機");
                try
                {
                    if (!File.Exists(_path)) return;
                    Dictionary<string, object> root = JsonUtil.Parse(File.ReadAllText(_path));
                    object rawGroups;
                    if (root.TryGetValue("groups", out rawGroups) && rawGroups is IEnumerable)
                    {
                        foreach (object item in (IEnumerable)rawGroups)
                        {
                            string group = Convert.ToString(item).Trim();
                            if (group.Length > 0) EnsureGroup(group);
                        }
                    }
                    object rawDevices;
                    if (root.TryGetValue("devices", out rawDevices) && rawDevices is IEnumerable)
                    {
                        foreach (object item in (IEnumerable)rawDevices)
                        {
                            Dictionary<string, object> data = item as Dictionary<string, object>;
                            if (data == null) continue;
                            string mode = JsonUtil.GetString(data, "mode", "lan").Trim().ToLowerInvariant();
                            string ip = JsonUtil.GetString(data, "ip", "").Trim();
                            string endpoint = NormalizeOnlineEndpoint(JsonUtil.GetString(data, "endpoint", ""));
                            if (mode == "online")
                            {
                                if (endpoint.Length == 0) continue;
                            }
                            else
                            {
                                mode = "lan";
                                if (ip.Length == 0 || !NetworkUtils.IsPrivateOrLinkLocal(ip)) continue;
                            }
                            DeviceProfile device = new DeviceProfile
                            {
                                Id = JsonUtil.GetString(data, "id", Guid.NewGuid().ToString("N")),
                                Name = JsonUtil.GetString(data, "name", "遠端電腦"),
                                Ip = ip,
                                ConnectionMode = mode,
                                OnlineEndpoint = endpoint,
                                ProtectedOnlineKey = JsonUtil.GetString(data, "onlineKeyProtected", ""),
                                RegistryUrl = NormalizeRegistryUrl(JsonUtil.GetString(data, "registryUrl", "")),
                                RegistryDeviceId = JsonUtil.GetString(data, "registryDeviceId", ""),
                                ProtectedRegistrySecret = JsonUtil.GetString(data, "registrySecretProtected", ""),
                                Group = NormalizeGroup(JsonUtil.GetString(data, "group", "未分類")),
                                Favorite = JsonUtil.GetBool(data, "favorite", false),
                                LastSeenUtc = JsonUtil.GetString(data, "lastSeenUtc", "")
                            };
                            object rawTags;
                            if (data.TryGetValue("tags", out rawTags) && rawTags is IEnumerable)
                                foreach (object tag in (IEnumerable)rawTags) AddTag(device.Tags, Convert.ToString(tag));
                            EnsureGroup(device.Group);
                            _devices.Add(device);
                        }
                    }
                }
                catch { }
            }
        }

        private void SaveLocked()
        {
            string dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonUtil.Serialize(new
            {
                groups = _groups.ToArray(),
                devices = _devices.Select(d => new
                {
                    id = d.Id,
                    name = d.Name,
                    ip = d.Ip,
                    mode = d.ConnectionMode,
                    endpoint = d.OnlineEndpoint,
                    onlineKeyProtected = d.ProtectedOnlineKey,
                    registryUrl = d.RegistryUrl,
                    registryDeviceId = d.RegistryDeviceId,
                    registrySecretProtected = d.ProtectedRegistrySecret,
                    group = d.Group,
                    tags = d.Tags.ToArray(),
                    favorite = d.Favorite,
                    lastSeenUtc = d.LastSeenUtc
                }).ToArray()
            }));
        }

        private void EnsureGroup(string group)
        {
            if (_groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase))) return;
            _groups.Add(group);
        }

        private static string NormalizeGroup(string group)
        {
            group = (group ?? "").Trim();
            return group.Length == 0 ? "未分類" : group;
        }

        private static List<string> ParseTags(string csv)
        {
            List<string> tags = new List<string>();
            foreach (string raw in (csv ?? "").Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)) AddTag(tags, raw);
            return tags;
        }

        private static void AddTag(List<string> tags, string raw)
        {
            string tag = (raw ?? "").Trim();
            if (tag.Length == 0 || tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) return;
            tags.Add(tag);
        }

        private static DeviceProfile Clone(DeviceProfile source)
        {
            return new DeviceProfile
            {
                Id = source.Id,
                Name = source.Name,
                Ip = source.Ip,
                ConnectionMode = source.ConnectionMode,
                OnlineEndpoint = source.OnlineEndpoint,
                ProtectedOnlineKey = source.ProtectedOnlineKey,
                RegistryUrl = source.RegistryUrl,
                RegistryDeviceId = source.RegistryDeviceId,
                ProtectedRegistrySecret = source.ProtectedRegistrySecret,
                Group = source.Group,
                Tags = new List<string>(source.Tags),
                Favorite = source.Favorite,
                LastSeenUtc = source.LastSeenUtc
            };
        }

        private static string NormalizeOnlineEndpoint(string endpoint)
        {
            endpoint = (endpoint ?? "").Trim().TrimEnd('/');
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) || !string.Equals(uri.Scheme, "tcp", StringComparison.OrdinalIgnoreCase) || uri.Port <= 0) return "";
            return "tcp://" + uri.Host + ":" + uri.Port;
        }

        private static string NormalizeRegistryUrl(string url)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            if (url.Length == 0) return "";
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.Host)) return "";
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("ChaoChaoKvm.Online.Device.v1");

        private static string ProtectSecret(string secret)
        {
            byte[] raw = Encoding.UTF8.GetBytes(secret ?? "");
            byte[] protectedBytes = ProtectedData.Protect(raw, SecretEntropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string UnprotectSecret(string protectedText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(protectedText)) return "";
                byte[] protectedBytes = Convert.FromBase64String(protectedText);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, SecretEntropy, DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }
    }
}
