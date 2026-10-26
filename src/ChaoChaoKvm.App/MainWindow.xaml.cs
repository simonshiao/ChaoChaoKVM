using System.IO;
using System.Windows;
using ChaoChaoKvm.Api;
using ChaoChaoKvm.Bridge;
using ChaoChaoKvm.Services;
using Microsoft.Web.WebView2.Core;

namespace ChaoChaoKvm;

public partial class MainWindow : Window
{
    private readonly AppStateService _state = new();
    private readonly ComputerControlService _computer = new();
    private BrowserBridge? _bridge;
    private AiControlApiServer? _aiApi;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
            string indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("找不到 Web UI：index.html", indexPath);

            await Web.EnsureCoreWebView2Async();
            if (Web.CoreWebView2 is null)
                throw new InvalidOperationException("WebView2 初始化失敗。");

            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;

            _bridge = new BrowserBridge(Web, _state);
            _bridge.Attach();

            _aiApi = new AiControlApiServer(_computer);
            await _aiApi.StartAsync();

            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.chaokvm.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            Web.Source = new Uri("https://app.chaokvm.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"ChaoChao KVM 啟動失敗：\n{ex.Message}",
                "ChaoChao KVM",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_aiApi is not null)
            await _aiApi.DisposeAsync();
    }
}
