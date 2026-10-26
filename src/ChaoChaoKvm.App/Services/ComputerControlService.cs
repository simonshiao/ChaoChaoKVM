using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ChaoChaoKvm.Services;

public sealed class ComputerControlService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventfKeyUp = 0x0002;
    private const uint KeyEventfUnicode = 0x0004;
    private const int SwRestore = 9;

    public string OpenTextFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path 不可為空白。", nameof(path));

        string fullPath = Path.GetFullPath(path.Trim());
        if (!string.Equals(Path.GetExtension(fullPath), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI API 目前只允許開啟 .txt 文件。");

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (!File.Exists(fullPath))
            File.WriteAllText(fullPath, string.Empty);

        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = $"\"{fullPath}\"",
            UseShellExecute = true
        });

        if (process is null)
            throw new InvalidOperationException("無法啟動記事本。");

        try
        {
            process.WaitForInputIdle(3000);
        }
        catch (InvalidOperationException)
        {
            // Some Windows builds return before a GUI input queue is available.
        }

        for (int i = 0; i < 20; i++)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(process.MainWindowHandle, SwRestore);
                SetForegroundWindow(process.MainWindowHandle);
                break;
            }

            Thread.Sleep(100);
        }

        return fullPath;
    }

    public int TypeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
            return 0;

        var inputs = new INPUT[text.Length * 2];
        int index = 0;
        foreach (char ch in text)
        {
            inputs[index++] = CreateUnicodeInput(ch, keyUp: false);
            inputs[index++] = CreateUnicodeInput(ch, keyUp: true);
        }

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput 僅送出 {sent}/{inputs.Length} 個鍵盤事件。Win32={Marshal.GetLastWin32Error()}");

        return text.Length;
    }

    private static INPUT CreateUnicodeInput(char ch, bool keyUp)
    {
        return new INPUT
        {
            type = InputKeyboard,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KeyEventfUnicode | (keyUp ? KeyEventfKeyUp : 0),
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
