using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace ChaoChaoKvm.Core
{
    public static class JsonUtil
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        public static string Serialize(object value) { return Serializer.Serialize(value); }
        public static byte[] Utf8(object value) { return Encoding.UTF8.GetBytes(Serialize(value)); }
        public static Dictionary<string, object> Parse(string text) { return Serializer.Deserialize<Dictionary<string, object>>(text); }
        public static Dictionary<string, object> Parse(byte[] data) { return Parse(Encoding.UTF8.GetString(data)); }

        public static string GetString(Dictionary<string, object> data, string key, string fallback)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null ? System.Convert.ToString(value) : fallback;
        }

        public static int GetInt(Dictionary<string, object> data, string key, int fallback)
        {
            object value;
            int result;
            return data != null && data.TryGetValue(key, out value) && value != null && int.TryParse(System.Convert.ToString(value), out result) ? result : fallback;
        }

        public static bool GetBool(Dictionary<string, object> data, string key, bool fallback)
        {
            object value;
            bool result;
            return data != null && data.TryGetValue(key, out value) && value != null && bool.TryParse(System.Convert.ToString(value), out result) ? result : fallback;
        }
    }
}
