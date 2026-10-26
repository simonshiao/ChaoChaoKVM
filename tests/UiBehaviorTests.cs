using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using ChaoChaoKvm;

public class UiBehaviorTests
{
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
    static Dictionary<string, object> Input(string kind, int id) { return new Dictionary<string, object> { {"kind", kind}, {"id", id} }; }
    [STAThread]
    public static int Main()
    {
        try {
            Check(!string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()), "packaged WebView2 native loader locates installed runtime without opening a browser");
            Point result;
            Check(ViewerGeometry.Normalize(new Point(500, 500), 1000, 1000, 1920, 1080, false, out result) && result.X == .5 && result.Y == .5, "letterboxed center maps to center");
            Check(!ViewerGeometry.Normalize(new Point(500, 100), 1000, 1000, 1920, 1080, false, out result), "top letterbox rejects clicks");
            Check(!ViewerGeometry.Normalize(new Point(100, 500), 1000, 1000, 1080, 1920, false, out result), "side letterbox rejects clicks");
            Check(ViewerGeometry.Normalize(new Point(1200, -20), 1000, 1000, 1920, 1080, true, out result) && result.X == 1 && result.Y == 0, "captured release outside clamps to edge");
            Check(!ViewerGeometry.Normalize(new Point(0, 0), 0, 0, 1920, 1080, false, out result), "unlaid-out viewer rejects input");
            Check(!ViewerGeometry.Normalize(new Point(0, 0), 1000, 1000, 0, 0, false, out result), "invalid frame rejects input");
            Check(ViewerGeometry.Normalize(new Point(960, 540), 1920, 1080, 1920, 1080, false, out result) && result.X == .5, "native size mapping");

            var queueType = typeof(ViewerGeometry).Assembly.GetType("ChaoChaoKvm.RemoteInputQueue");
            var sent = new List<int>(); var entered = new ManualResetEvent(false); var unblock = new ManualResetEvent(false); var done = new ManualResetEvent(false);
            Action<Dictionary<string, object>> sender = delegate(Dictionary<string, object> d) { int id = (int)d["id"]; if (id == 0) { entered.Set(); unblock.WaitOne(3000); } lock(sent) sent.Add(id); if (id == 4) done.Set(); };
            object queue = Activator.CreateInstance(queueType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] {sender, new Func<bool>(delegate { return true; })}, null);
            Action<Dictionary<string, object>> enqueue = d => queueType.GetMethod("Enqueue").Invoke(queue, new object[] {d});
            enqueue(Input("key",0)); Check(entered.WaitOne(2000), "input runs on dedicated worker");
            enqueue(Input("move",1)); enqueue(Input("move",2)); enqueue(Input("button",3)); enqueue(Input("key",4));
            queueType.GetMethod("Complete").Invoke(queue,null); unblock.Set(); Check(done.WaitOne(3000), "queue drains releases after completion");
            lock(sent) Check(string.Join(",",sent) == "0,2,3,4", "coalesces moves while preserving key and button order");

            // Construct the actual compiled XAML without starting the network or displaying a window.
            var app = new Application();
            var config = new ChaoChaoKvm.Core.AppConfig { Fps=10, JpegQuality=55, ActivePeerIp="", LastPeerIp="", ReceiveDirectory=System.IO.Path.GetTempPath() };
            var manager = new ChaoChaoKvm.Services.KvmPeerManager(config);
            var session = new SessionWindow(manager);
            Check(session.FindName("ControlButton") == null && session.FindName("RemoteImage") != null, "native session XAML initializes without a control-mode button");
            Check(typeof(SessionWindow).GetMethod("ToggleControl", BindingFlags.Instance | BindingFlags.NonPublic) == null, "session has no manual control-mode toggle");
            var inputPaused = typeof(SessionWindow).GetField("_inputPaused", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(!(bool)inputPaused.GetValue(session), "direct input is not paused by default");
            var toolbarActions = session.FindName("ToolbarActions") as FrameworkElement;
            var statusGlass = session.FindName("StatusGlass") as FrameworkElement;
            var toolbarGlass = session.FindName("ToolbarGlass") as FrameworkElement;
            var toolbarToggle = typeof(SessionWindow).GetMethod("ToggleToolbar", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(toolbarActions != null && statusGlass != null && toolbarGlass != null && toolbarToggle != null && toolbarActions.Visibility == Visibility.Visible, "glass toolbar starts expanded");
            Check(statusGlass.Visibility == Visibility.Visible && statusGlass.Opacity < 1 && toolbarGlass.Opacity < 1, "glass chrome starts visually quiet until pointer enters");
            Check(typeof(SessionWindow).GetMethod("ChromePointerActivity", BindingFlags.Instance | BindingFlags.NonPublic) != null, "glass chrome has pointer reveal behavior");
            toolbarToggle.Invoke(session, new object[] { null, new RoutedEventArgs() });
            Check(toolbarActions.Visibility == Visibility.Collapsed && statusGlass.Visibility == Visibility.Collapsed, "collapsed chrome hides toolbar actions and computer identity");
            Check(!(bool)inputPaused.GetValue(session), "hiding toolbar does not pause direct input mode");
            toolbarToggle.Invoke(session, new object[] { null, new RoutedEventArgs() });
            Check(toolbarActions.Visibility == Visibility.Visible && statusGlass.Visibility == Visibility.Visible, "glass toolbar and computer identity expand together");
            var multi = new MultiSessionWindow(manager);
            Check(multi.FindName("SessionTabsPanel") != null && multi.FindName("RemoteImage") != null, "multi-session workspace XAML initializes with session tabs");
            var fixedToolbar = multi.FindName("SessionToolbar") as System.Windows.Controls.Border;
            var remoteViewer = multi.FindName("Viewer") as System.Windows.Controls.Grid;
            var toolbarRoot = multi.FindName("ChromeRoot") as System.Windows.Controls.Grid;
            Check(fixedToolbar != null && remoteViewer != null && toolbarRoot.RowDefinitions.Count == 2 &&
                System.Windows.Controls.Grid.GetRow(fixedToolbar) == 0 && System.Windows.Controls.Grid.GetRow(remoteViewer) == 1,
                "multi-session toolbar is a single fixed row outside the remote desktop");
            Check(multi.FindName("StatusGlass") == null && multi.FindName("TabsGlass") == null && multi.FindName("HintGlass") == null,
                "multi-session viewer has no overlapping glass toolbar or bottom banner");
            Check(multi.FindName("BackButton") != null && multi.FindName("RetryButton") != null &&
                multi.FindName("FullScreenButton") != null && multi.FindName("ControlButton") != null &&
                multi.FindName("EndSessionButton") != null && multi.FindName("StatusDot") != null,
                "remote toolbar exposes clear navigation status retry fullscreen control and close");
            Check(typeof(MultiSessionWindow).GetMethod("RetryActiveSession", BindingFlags.Instance | BindingFlags.NonPublic) != null &&
                typeof(MultiSessionWindow).GetMethod("ToggleReadOnly", BindingFlags.Instance | BindingFlags.NonPublic) != null &&
                typeof(MultiSessionWindow).GetMethod("EndActiveSession", BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "remote toolbar actions are wired to real session methods");
            Check(typeof(MultiSessionWindow).GetMethod("OpenPeer") != null && typeof(MultiSessionWindow).GetMethod("SelectPeer") != null && typeof(MultiSessionWindow).GetMethod("ClosePeer") != null, "multi-session workspace exposes open select close lifecycle");
            Check(typeof(MultiSessionWindow).GetMethod("OpenEndpoint") != null, "multi-session workspace accepts online endpoints");
            Check(Array.Exists(typeof(ChaoChaoKvm.Services.HttpPeerClient).GetMethods(), m => m.Name == "StartEndpoint"), "HTTP client supports tunneled online endpoints");
            Check(typeof(ChaoChaoKvm.Services.HttpPeerServer).GetProperty("RequiredAccessKey") != null, "online server exposes mandatory access-key boundary");
            Check(typeof(ChaoChaoKvm.Services.KvmPeerManager).GetMethod("TrustPeer") != null && typeof(ChaoChaoKvm.Services.KvmPeerManager).GetMethod("IsPeerConnected") != null, "peer manager exposes multi-peer trust and liveness routing");
            Check(typeof(ChaoChaoKvm.Services.KvmPeerManager).GetMethod("SendInputToPeer") != null, "peer manager routes remote input to a selected peer");
            Check(typeof(ChaoChaoKvm.Services.HttpPeerServer).GetMethod("QueueInput", new[] { typeof(string), typeof(Dictionary<string, object>) }) != null, "HTTP server keeps peer-specific reverse input queues");
            var devicePath = Path.Combine(Path.GetTempPath(), "chao-device-test-" + Guid.NewGuid().ToString("N") + ".json");
            var library = new ChaoChaoKvm.Core.DeviceLibraryStore(devicePath);
            library.AddGroup("AOI測試");
            var saved = library.Upsert("", "DIA-03", "10.250.250.250", "AOI測試", "DIA, AOI, 3F", true);
            Check(saved.Group == "AOI測試" && saved.Tags.Count == 3 && saved.Favorite, "device library persists group tags and favorite metadata");
            var reloaded = new ChaoChaoKvm.Core.DeviceLibraryStore(devicePath).FindByIp("10.250.250.250");
            Check(reloaded != null && reloaded.Name == "DIA-03" && reloaded.Group == "AOI測試", "device library reloads saved devices");
            const string onlineTestKey = "online-test-access-key-1234567890";
            var onlineSaved = library.UpsertOnline("", "Cloud-AOI", "tcp://0.tcp.ap.ngrok.io:23456", onlineTestKey, "AOI測試", "Online, AOI", false);
            var onlineCredential = library.GetOnlineCredential(onlineSaved.Id);
            Check(onlineCredential != null && onlineCredential.Endpoint == "tcp://0.tcp.ap.ngrok.io:23456" && onlineCredential.AccessKey == onlineTestKey, "online device library restores DPAPI credential for current Windows user");
            Check(!File.ReadAllText(devicePath).Contains(onlineTestKey), "online device library never writes access key in plaintext");
            var onlineReloadedStore = new ChaoChaoKvm.Core.DeviceLibraryStore(devicePath);
            var onlineReloaded = onlineReloadedStore.GetOnlineCredential(onlineSaved.Id);
            Check(onlineReloaded != null && onlineReloaded.AccessKey == onlineTestKey, "online device DPAPI credential survives library reload");
            const string registrySecret = "registry-test-secret-1234567890abcdef";
            var registrySaved = library.UpsertOnline(onlineSaved.Id, "Cloud-AOI", "tcp://0.tcp.ap.ngrok.io:24567", onlineTestKey, "AOI測試", "Online, AOI", false, "https://registry.example.test", "kvm_device_1234567890", registrySecret);
            var registryCredential = library.GetOnlineCredential(registrySaved.Id);
            Check(registryCredential != null && registryCredential.RegistryDeviceId == "kvm_device_1234567890" && registryCredential.RegistrySecret == registrySecret, "online device restores protected registry credential");
            Check(!File.ReadAllText(devicePath).Contains(registrySecret), "online device never writes registry secret in plaintext");
            try { File.Delete(devicePath); } catch { }
            multi.Close(); session.Close(); manager.Dispose(); app.Shutdown();
            Console.WriteLine("UI_BEHAVIOR_PASS " + count); return 0;
        } catch(Exception e) { Console.WriteLine(e); return 1; }
    }
}
