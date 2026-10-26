using ChaoChaoKvm.Core;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ChaoChaoKvm.Services
{
    public sealed class InputInjectionService
    {
        private const uint MouseLeftDown = 0x0002;
        private const uint MouseLeftUp = 0x0004;
        private const uint MouseRightDown = 0x0008;
        private const uint MouseRightUp = 0x0010;
        private const uint MouseMiddleDown = 0x0020;
        private const uint MouseMiddleUp = 0x0040;
        private const uint MouseWheel = 0x0800;
        private const uint KeyUp = 0x0002;

        public void Apply(byte[] payload, ScreenInfo localScreen)
        {
            Dictionary<string, object> data = JsonUtil.Parse(payload);
            string kind = JsonUtil.GetString(data, "kind", "");
            if (kind == "move" || kind == "button" || kind == "wheel")
            {
                double nx = GetDouble(data, "x", 0);
                double ny = GetDouble(data, "y", 0);
                nx = Math.Max(0, Math.Min(1, nx));
                ny = Math.Max(0, Math.Min(1, ny));
                int x = localScreen.Left + (int)Math.Round(nx * Math.Max(0, localScreen.Width - 1));
                int y = localScreen.Top + (int)Math.Round(ny * Math.Max(0, localScreen.Height - 1));
                SetCursorPos(x, y);

                if (kind == "button")
                {
                    int button = JsonUtil.GetInt(data, "button", 1);
                    bool down = JsonUtil.GetBool(data, "down", false);
                    uint flag = button == 2 ? (down ? MouseRightDown : MouseRightUp)
                              : button == 3 ? (down ? MouseMiddleDown : MouseMiddleUp)
                              : (down ? MouseLeftDown : MouseLeftUp);
                    mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
                }
                else if (kind == "wheel")
                {
                    int delta = JsonUtil.GetInt(data, "delta", 0);
                    mouse_event(MouseWheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                }
                return;
            }

            if (kind == "key")
            {
                int vk = JsonUtil.GetInt(data, "vk", 0);
                bool down = JsonUtil.GetBool(data, "down", false);
                if (vk > 0 && vk < 256) keybd_event((byte)vk, 0, down ? 0u : KeyUp, UIntPtr.Zero);
            }
        }

        private static double GetDouble(Dictionary<string, object> data, string key, double fallback)
        {
            object value;
            double result;
            return data.TryGetValue(key, out value) && value != null && double.TryParse(Convert.ToString(value), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result)
                ? result : fallback;
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    }
}
