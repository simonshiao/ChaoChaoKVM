using ChaoChaoKvm.Core;
using ChaoChaoKvm.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ChaoChaoKvm.Api
{
    // 只轉接 KVM 畫面與既有鍵鼠協定；不另建 MCP、Agent、PowerShell 或對外服務。
    public sealed class KvmComputerUseAdapter : IDisposable
    {
        private sealed class Frame { public byte[] Jpeg; public ScreenInfo Info; public DateTime At; }
        private readonly KvmPeerManager _manager;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Frame> _frames = new Dictionary<string, Frame>(StringComparer.OrdinalIgnoreCase);
        public KvmComputerUseAdapter(KvmPeerManager manager)
        {
            _manager = manager;
            manager.RemoteFrameFromPeer += OnFrame;
            manager.RemoteInfoFromPeer += OnInfo;
        }
        private void OnFrame(string peer, byte[] jpeg)
        {
            if (string.IsNullOrEmpty(peer) || jpeg == null || jpeg.Length == 0) return;
            lock (_gate) { Frame f = Find(peer); f.Jpeg = jpeg; f.At = DateTime.UtcNow; }
        }
        private void OnInfo(string peer, ScreenInfo info)
        {
            if (string.IsNullOrEmpty(peer) || info == null) return;
            lock (_gate) Find(peer).Info = info;
        }
        private Frame Find(string peer)
        {
            Frame f;
            if (!_frames.TryGetValue(peer, out f)) _frames[peer] = f = new Frame();
            return f;
        }
        private string Peer(Dictionary<string, object> q)
        {
            string peer = JsonUtil.GetString(q, "peer", _manager.ActivePeerIp).Trim();
            if (peer.Length == 0 || !NetworkUtils.IsPrivateOrLinkLocal(peer) ||
                NetworkUtils.IsLocalAddress(peer) || !_manager.IsPeerConnected(peer))
                throw new InvalidOperationException("指定的 KVM peer 未連線。");
            return peer;
        }
        private Frame Fresh(string peer)
        {
            lock (_gate)
            {
                Frame f = Find(peer);
                if (f.Jpeg == null || f.Info == null || f.Info.Width < 2 || f.Info.Height < 2 ||
                    (DateTime.UtcNow - f.At).TotalSeconds > 5)
                    throw new InvalidOperationException("尚無遠端畫面，或畫面超過 5 秒未更新。");
                return f;
            }
        }
        private static Dictionary<string, object> Done(string peer)
        {
            return new Dictionary<string, object> { { "ok", true }, { "peer", peer }, { "queued", true },
                { "note", "KVM 已排入遠端輸入，請以後續畫面驗證實際操作。" } };
        }
        public Dictionary<string, object> Status(Dictionary<string, object> q)
        {
            string peer = Peer(q); Frame f;
            lock (_gate) f = Find(peer);
            return new Dictionary<string, object> { { "ok", true }, { "peer", peer },
                { "frame_ready", f.Jpeg != null && f.Info != null && (DateTime.UtcNow - f.At).TotalSeconds < 5 },
                { "machine", f.Info == null ? "" : f.Info.Machine },
                { "width", f.Info == null ? 0 : f.Info.Width }, { "height", f.Info == null ? 0 : f.Info.Height } };
        }
        public Dictionary<string, object> Screenshot(Dictionary<string, object> q)
        {
            string peer = Peer(q); Frame f = Fresh(peer);
            // 避免巨大的影格使 JSON line 或 WebView2/MSBuild 周邊資源耗盡。
            if (f.Jpeg.Length > 3 * 1024 * 1024)
                throw new InvalidOperationException("遠端影格超過 3 MB，請先降低 KVM JPEG 畫質。");
            return new Dictionary<string, object> { { "ok", true }, { "peer", peer }, { "format", "jpeg" },
                { "width", f.Info.Width }, { "height", f.Info.Height },
                { "captured_utc", f.At.ToString("o") }, { "image_base64", Convert.ToBase64String(f.Jpeg) } };
        }
        private static int Coord(Dictionary<string, object> q, string name, int limit)
        {
            int value = JsonUtil.GetInt(q, name, -1);
            if (value < 0 || value >= limit) throw new InvalidOperationException(name + " 座標超出遠端畫面範圍。");
            return value;
        }
        private static Dictionary<string, object> Pointer(string kind, double x, double y)
        {
            return new Dictionary<string, object> {
                { "kind", kind }, { "x", x.ToString("R", CultureInfo.InvariantCulture) },
                { "y", y.ToString("R", CultureInfo.InvariantCulture) } };
        }
        public Dictionary<string, object> Mouse(Dictionary<string, object> q, string command)
        {
            string peer = Peer(q); Frame f = Fresh(peer);
            double x = (double)Coord(q, "x", f.Info.Width) / (f.Info.Width - 1);
            double y = (double)Coord(q, "y", f.Info.Height) / (f.Info.Height - 1);
            if (command == "computer_move") _manager.SendInputToPeer(peer, Pointer("move", x, y));
            else if (command == "computer_scroll")
            {
                int delta = JsonUtil.GetInt(q, "delta", 0);
                if (delta == 0 || Math.Abs(delta) > 1200) throw new InvalidOperationException("delta 必須為 -1200 到 1200 的非零值。");
                var input = Pointer("wheel", x, y); input["delta"] = delta;
                _manager.SendInputToPeer(peer, input);
            }
            else
            {
                string side = JsonUtil.GetString(q, "button", "left");
                int button = side == "left" ? 1 : side == "right" ? 2 : side == "middle" ? 3 : 0;
                int count = JsonUtil.GetInt(q, "clicks", 1);
                if (button == 0 || count < 1 || count > 2) throw new InvalidOperationException("僅支援左右中鍵，單擊或雙擊。");
                for (int i = 0; i < count; i++)
                {
                    var down = Pointer("button", x, y); down["button"] = button; down["down"] = true;
                    var up = Pointer("button", x, y); up["button"] = button; up["down"] = false;
                    _manager.SendInputToPeer(peer, down); _manager.SendInputToPeer(peer, up);
                    if (i == 0 && count == 2) Thread.Sleep(90);
                }
            }
            return Done(peer);
        }
        private static int Vk(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            var special = new Dictionary<string, int> { { "enter", 13 }, { "tab", 9 },
                { "esc", 27 }, { "escape", 27 }, { "backspace", 8 }, { "delete", 46 },
                { "space", 32 }, { "ctrl", 17 }, { "control", 17 }, { "alt", 18 },
                { "shift", 16 }, { "win", 91 }, { "up", 38 }, { "down", 40 },
                { "left", 37 }, { "right", 39 }, { "home", 36 }, { "end", 35 },
                { "pageup", 33 }, { "pagedown", 34 }, { "insert", 45 } };
            int v; if (special.TryGetValue(n, out v)) return v;
            if (n.Length == 1 && ((n[0] >= 'a' && n[0] <= 'z') || (n[0] >= '0' && n[0] <= '9')))
                return char.ToUpperInvariant(n[0]);
            if (n.StartsWith("f") && int.TryParse(n.Substring(1), out v) && v >= 1 && v <= 12) return 111 + v;
            throw new InvalidOperationException("無法識別按鍵：" + name);
        }
        private void Stroke(string peer, int vk, bool down)
        {
            _manager.SendInputToPeer(peer, new Dictionary<string, object> { { "kind", "key" },
                { "vk", vk }, { "down", down } });
        }
        public Dictionary<string, object> Keyboard(Dictionary<string, object> q, string command)
        {
            string peer = Peer(q);
            Fresh(peer); // 沒有最新畫面時不接受盲目鍵盤輸入。
            if (command == "computer_type")
            {
                string text = JsonUtil.GetString(q, "text", "");
                if (text.Length < 1 || text.Length > 2048) throw new InvalidOperationException("文字輸入限 1~2048 字。");
                _manager.SendInputToPeer(peer, new Dictionary<string, object> { { "kind", "unicode" }, { "text", text } });
            }
            else if (command == "computer_key")
            {
                int vk = Vk(JsonUtil.GetString(q, "key", ""));
                Stroke(peer, vk, true); Stroke(peer, vk, false);
            }
            else
            {
                object raw; IEnumerable keys = q.TryGetValue("keys", out raw) ? raw as IEnumerable : null;
                if (keys == null || raw is string) throw new InvalidOperationException("keys 必須是陣列。");
                var codes = new List<int>();
                foreach (object item in keys) codes.Add(Vk(Convert.ToString(item)));
                if (codes.Count < 2 || codes.Count > 4) throw new InvalidOperationException("hotkey 限 2~4 個按鍵。");
                try { foreach (int vk in codes) Stroke(peer, vk, true); }
                finally { for (int i = codes.Count - 1; i >= 0; --i) Stroke(peer, codes[i], false); }
            }
            return Done(peer);
        }
        public void Dispose()
        {
            _manager.RemoteFrameFromPeer -= OnFrame;
            _manager.RemoteInfoFromPeer -= OnInfo;
        }
    }
}
