using System.Text.Json;
using ChaoChaoKvm.Services;
using Microsoft.Web.WebView2.Wpf;

namespace ChaoChaoKvm.Bridge;

public sealed class BrowserBridge
{
    private readonly WebView2 _web;
    private readonly AppStateService _state;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public BrowserBridge(WebView2 web, AppStateService state)
    {
        _web = web;
        _state = state;
    }

    public void Attach()
    {
        _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
    }

    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out Uri? source) ||
                !string.Equals(source.Host, "app.chaokvm.local", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒絕非 ChaoChao KVM UI 的 Web 訊息。");

            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            string action = doc.RootElement.TryGetProperty("action", out JsonElement actionNode)
                ? actionNode.GetString() ?? ""
                : "";

            switch (action)
            {
                case "app.getState":
                    break;

                case "host.start":
                    _state.StartHost();
                    break;

                case "host.stop":
                    _state.StopHost();
                    break;

                case "target.set":
                    string ip = doc.RootElement.TryGetProperty("ip", out JsonElement ipNode)
                        ? ipNode.GetString() ?? ""
                        : "";
                    int port = doc.RootElement.TryGetProperty("port", out JsonElement portNode) && portNode.TryGetInt32(out int value)
                        ? value
                        : 45888;
                    _state.SetTarget(ip, port);
                    break;

                default:
                    throw new InvalidOperationException($"不支援的 UI action：{action}");
            }

            await PushStateAsync();
        }
        catch (Exception ex)
        {
            await PostAsync(new { type = "bridge.error", message = ex.Message });
        }
    }

    private Task PushStateAsync() => PostAsync(new { type = "app.state", state = _state.Snapshot() });

    private Task PostAsync(object payload)
    {
        string json = JsonSerializer.Serialize(payload, _json);
        _web.CoreWebView2.PostWebMessageAsJson(json);
        return Task.CompletedTask;
    }
}
