using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace ChaoChaoKvm.Services
{
    public sealed class OnlineRegistryResult
    {
        public bool Ok { get; set; }
        public bool Online { get; set; }
        public string Endpoint { get; set; }
        public string Name { get; set; }
        public double AgeSeconds { get; set; }
        public string Error { get; set; }
    }

    public sealed class OnlineRegistryClient
    {
        private const string Prefix = "/kvm-registry/v1";

        public void Register(string registryUrl, string deviceId, string registrySecret, string endpoint, string name)
        {
            string root = NormalizeRoot(registryUrl);
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(registrySecret)) throw new InvalidOperationException("Registry 裝置識別資料不完整。");
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "device_id", deviceId },
                { "endpoint", endpoint ?? "" },
                { "name", string.IsNullOrWhiteSpace(name) ? "Online KVM" : name.Trim() }
            };
            Dictionary<string, object> response = SendJson(root + Prefix + "/register", "POST", registrySecret, payload, 3500);
            if (!JsonUtil.GetBool(response, "ok", false)) throw new InvalidOperationException("Registry 拒絕更新：" + JsonUtil.GetString(response, "error", "unknown"));
        }

        public OnlineRegistryResult Resolve(string registryUrl, string deviceId, string registrySecret)
        {
            string root = NormalizeRoot(registryUrl);
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(registrySecret))
                return new OnlineRegistryResult { Error = "registry credential missing" };
            try
            {
                string url = root + Prefix + "/resolve?id=" + Uri.EscapeDataString(deviceId.Trim());
                Dictionary<string, object> response = SendJson(url, "GET", registrySecret, null, 3000);
                bool ok = JsonUtil.GetBool(response, "ok", false);
                return new OnlineRegistryResult
                {
                    Ok = ok,
                    Online = JsonUtil.GetBool(response, "online", false),
                    Endpoint = JsonUtil.GetString(response, "endpoint", ""),
                    Name = JsonUtil.GetString(response, "name", ""),
                    AgeSeconds = GetDouble(response, "age_seconds"),
                    Error = ok ? "" : JsonUtil.GetString(response, "error", "unknown")
                };
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                return new OnlineRegistryResult { Error = response == null ? ex.Message : "HTTP " + (int)response.StatusCode };
            }
            catch (Exception ex) { return new OnlineRegistryResult { Error = ex.Message }; }
        }

        public bool Health(string registryUrl)
        {
            try
            {
                Dictionary<string, object> response = SendJson(NormalizeRoot(registryUrl) + Prefix + "/health", "GET", "", null, 2500);
                return JsonUtil.GetBool(response, "ok", false);
            }
            catch { return false; }
        }

        private static Dictionary<string, object> SendJson(string url, string method, string registrySecret, object payload, int timeout)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.KeepAlive = false;
            request.UserAgent = "ChaoChaoKvmOnline/2";
            request.Accept = "application/json";
            if (!string.IsNullOrWhiteSpace(registrySecret)) request.Headers["X-Chao-Registry-Secret"] = registrySecret.Trim();
            if (payload != null)
            {
                byte[] raw = JsonUtil.Utf8(payload);
                request.ContentType = "application/json; charset=utf-8";
                request.ContentLength = raw.Length;
                using (Stream output = request.GetRequestStream()) output.Write(raw, 0, raw.Length);
            }
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return JsonUtil.Parse(reader.ReadToEnd());
        }

        private static string NormalizeRoot(string registryUrl)
        {
            registryUrl = (registryUrl ?? "").Trim().TrimEnd('/');
            Uri uri;
            if (!Uri.TryCreate(registryUrl, UriKind.Absolute, out uri) || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.Host))
                throw new InvalidOperationException("Registry URL 必須是有效的 HTTPS 網址。");
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static double GetDouble(Dictionary<string, object> data, string key)
        {
            object raw;
            if (!data.TryGetValue(key, out raw) || raw == null) return 0;
            double value;
            return double.TryParse(Convert.ToString(raw), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }
}
