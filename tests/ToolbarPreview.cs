// Native layout preview: intentionally never starts HTTP, capture or input.
using System;
using System.Windows;
using ChaoChaoKvm;
using ChaoChaoKvm.Core;
using ChaoChaoKvm.Services;

internal static class ToolbarPreview
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        var config = new AppConfig { Fps = 10, JpegQuality = 55, ReceiveDirectory = System.IO.Path.GetTempPath() };
        var manager = new KvmPeerManager(config);
        var preview = new MultiSessionWindow(manager) { Title = "吵吵 KVM｜工具列版面檢查（未連線）" };
        preview.Closed += delegate { manager.Dispose(); };
        app.Run(preview);
    }
}
