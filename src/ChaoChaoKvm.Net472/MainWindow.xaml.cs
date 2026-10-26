using ChaoChaoKvm.Api;
using ChaoChaoKvm.Core;
using ChaoChaoKvm.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ChaoChaoKvm
{
    public partial class MainWindow : Window
    {
        private readonly AppConfig _config;
        private readonly KvmPeerManager _manager;
        private readonly DeviceLibraryStore _deviceLibrary;
        private readonly LocalAgentApi _agentApi;
        private readonly LanDiscoveryService _lanDiscovery;
        private CancellationTokenSource _connectCancellation;
        private readonly NgrokTunnelService _ngrok;
        private readonly OnlineRegistryClient _registryClient;
        private readonly CloudflareTunnelService _cloudflare;
        private readonly CloudflareRelayService _cloudflareRelay;
        private readonly CloudflareSettings _cloudflareSettings;
        private readonly Queue<string> _logs = new Queue<string>();
        private readonly DispatcherTimer _timer;
        private MultiSessionWindow _multiSession;
        private bool _webReady, _closed, _transferring, _openOnConnect;
        private string _error = "", _transfer = "尚無進行中的傳輸";
        private string _registryDetail = "裝置目錄尚未同步";
        private DateTime _nextRegistryPublishUtc = DateTime.MinValue;
        private int _registryPublishing;
        private readonly bool _preview;
        private const string Origin = "https://desktop.chaokvm.local";

        public MainWindow()
        {
            InitializeComponent();
            Title = BuildFlavor.IsOnline ? "吵吵 KVM Online" : "吵吵 KVM";
            _preview = Array.IndexOf(Environment.GetCommandLineArgs(), "--ui-preview") >= 0;
            _config = AppConfig.Load();
            _deviceLibrary = new DeviceLibraryStore();
            if (BuildFlavor.IsOnline && !string.IsNullOrWhiteSpace(_config.LastOnlineEndpoint) && !string.IsNullOrWhiteSpace(_config.LastOnlinePeerKey))
            {
                try
                {
                    _deviceLibrary.UpsertOnline("", "Online 遠端", _config.LastOnlineEndpoint, _config.LastOnlinePeerKey, "未分類", "Online", false);
                    _config.LastOnlinePeerKey = "";
                }
                catch { }
            }
            if (BuildFlavor.IsOnline) _config.Save();
            if (!string.IsNullOrWhiteSpace(_config.LastPeerIp)) _deviceLibrary.RememberConnection(_config.LastPeerIp, "");
            _manager = new KvmPeerManager(_config);
            _lanDiscovery = new LanDiscoveryService(_config.LanDeviceId, Environment.MachineName, _manager.Port);
            if (BuildFlavor.IsOnline)
            {
                _ngrok = new NgrokTunnelService();
                _registryClient = new OnlineRegistryClient();
                _ngrok.Log += Log;
                _ngrok.Changed += delegate { OnUi(delegate { PublishState(); TryPublishRegistry(true); }); };
            }
            if (BuildFlavor.IsCloudflare)
            {
                _cloudflareSettings = CloudflareSettingsStore.Load();
                _cloudflare = new CloudflareTunnelService();
                _cloudflareRelay = new CloudflareRelayService();
                _cloudflare.Log += Log;
                _cloudflare.Changed += delegate { OnUi(PublishState); };
                _cloudflareRelay.Log += Log;
                _cloudflareRelay.Changed += delegate { OnUi(PublishState); };
            }
            foreach (DeviceProfile device in _deviceLibrary.Devices) _manager.TrustPeer(device.Ip);
            _agentApi = new LocalAgentApi(_manager);
            _manager.Log += Log;
            _manager.FileProgress += delegate(long current, long total, string name) {
                OnUi(delegate { _transfer = (total > 0 ? (100.0 * current / total).ToString("0") + "% · " : "") + name + " · " + NetworkUtils.HumanSize(current) + " / " + NetworkUtils.HumanSize(total); });
            };
            _manager.RemoteInfoChanged += delegate(ScreenInfo info) {
                OnUi(delegate {
                    if (info != null && !string.IsNullOrWhiteSpace(_manager.ActivePeerIp)) _deviceLibrary.RememberConnection(_manager.ActivePeerIp, info.Machine);
                    PublishState();
                });
            };
            _manager.RemoteInfoFromPeer += delegate(string peerIp, ScreenInfo info) {
                OnUi(delegate {
                    if (info != null && !string.IsNullOrWhiteSpace(peerIp)) _deviceLibrary.RememberConnection(peerIp, info.Machine);
                    PublishState();
                });
            };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += delegate { PublishState(); if (BuildFlavor.IsOnline) TryPublishRegistry(false); if (_openOnConnect && _manager.IsPeerConnected(_manager.ActivePeerIp)) { _openOnConnect = false; OpenSession(); } };
            Loaded += OnLoaded;
            Closed += delegate { ShutdownServices(); };
        }

        public void RestoreExistingWindow()
        {
            if (_closed) return;
            if (_multiSession != null && _multiSession.IsVisible)
            {
                if (_multiSession.WindowState == WindowState.Minimized) _multiSession.WindowState = WindowState.Normal;
                _multiSession.Activate();
                return;
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
        }

        private void ShutdownServices()
        {
            if (_closed) return;
            _closed = true;
            _timer.Stop();
            if (_connectCancellation != null) _connectCancellation.Cancel();
            // A failure in one service must not prevent the HTTP listener,
            // capture workers and local Agent API from releasing their ports.
            try { if (_multiSession != null) _multiSession.Close(); } catch (Exception ex) { Debug.WriteLine(ex); }
            try { if (_ngrok != null) _ngrok.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            try { if (_cloudflare != null) _cloudflare.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            try { if (_cloudflareRelay != null) _cloudflareRelay.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            try { _agentApi.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            try { if (_lanDiscovery != null) _lanDiscovery.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            try { _manager.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            try { Web.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web");
                if (!File.Exists(Path.Combine(root, "index.html"))) throw new FileNotFoundException("缺少 Web\\index.html，請重新建置或複製完整程式資料夾。");
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BuildFlavor.IsOnline ? "ChaoChaoKvmOnline" : "ChaoChaoKvm", "WebView2");
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, profile);
                if (_closed) return;
                await Web.EnsureCoreWebView2Async(env);
                if (_closed) return;
                Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                Web.CoreWebView2.Settings.AreDevToolsEnabled = _preview;
                Web.CoreWebView2.Settings.IsZoomControlEnabled = false;
                Web.CoreWebView2.SetVirtualHostNameToFolderMapping("desktop.chaokvm.local", root, CoreWebView2HostResourceAccessKind.DenyCors);
                Web.CoreWebView2.NavigationStarting += delegate(object s, CoreWebView2NavigationStartingEventArgs a) { if (!a.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true; };
                Web.CoreWebView2.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs a) { a.Handled = true; };
                Web.CoreWebView2.WebMessageReceived += OnMessage;
                if (!_preview)
                {
                    try { _manager.Start(); } catch (Exception ex) { _error = ex.Message; Log(ex.ToString()); }
                    if (_manager.Running && !BuildFlavor.IsOnline)
                    {
                        try { _lanDiscovery.Start(); }
                        catch (Exception ex) { Log("附近電腦探索無法啟動，仍可用名稱解析或 IP 連線：" + ex.Message); }
                    }
                    try { _agentApi.Start(); } catch (Exception ex) { Log("Agent API 啟動失敗：" + ex.Message); }
                    if (BuildFlavor.IsOnline && _ngrok != null && _manager.Running)
                    {
                        try { _ngrok.Start(_manager.Port, _config.NgrokDomain); }
                        catch (Exception ex) { _error = "ngrok 啟動失敗：" + ex.Message; Log(ex.ToString()); }
                    }
                }
                else _error = "介面預覽模式：未啟動連線服務。";
                Web.Source = new Uri(Origin + "/index.html");
                _timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "介面無法啟動。請確認已安裝 Microsoft Edge WebView2 Runtime，並保留完整程式資料夾。\n\n" + ex.Message, "吵吵 KVM", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) return;
            string command = "";
            try
            {
                Dictionary<string, object> data = JsonUtil.Parse(e.WebMessageAsJson);
                command = JsonUtil.GetString(data, "command", "");
                if (command == "ready") { _webReady = true; PublishState(); PublishLogs(); return; }
                if (_preview) { Toast("介面預覽模式，未執行實際操作。"); return; }
                switch (command)
                {
                    case "connect":
                        string target = JsonUtil.GetString(data, "ip", "").Trim();
                        if (target.Length == 0) throw new InvalidOperationException("請輸入另一台電腦的名稱或 IP 位址。");
                        if (!_manager.Running) throw new InvalidOperationException(_error.Length > 0 ? _error : "連線服務尚未啟動。");
                        if (_connectCancellation != null) _connectCancellation.Cancel();
                        CancellationTokenSource attempt = new CancellationTokenSource();
                        _connectCancellation = attempt;
                        try
                        {
                            LanPeerAddress resolved = await _lanDiscovery.ResolveAsync(target, attempt.Token);
                            if (_closed || attempt.IsCancellationRequested) return;
                            _manager.SetActivePeer(resolved.Ip);
                            _deviceLibrary.RememberConnection(resolved.Ip, resolved.Name);
                            _openOnConnect = true;
                            Post(new { type = "connectResolved", ip = resolved.Ip, name = resolved.Name });
                        }
                        catch (OperationCanceledException) { return; }
                        finally { if (ReferenceEquals(_connectCancellation, attempt)) _connectCancellation = null; attempt.Dispose(); }
                        break;
                    case "discoverPeers": if (_lanDiscovery != null) _lanDiscovery.Search(); break;
                    case "copyMachineName": Clipboard.SetText(Environment.MachineName); Toast("已複製電腦名稱"); break;
                    case "retry": _manager.RetryNow(); Toast("已重新嘗試連線。"); break;
                    case "openSession": OpenSession(); break;
                    case "openDeviceSession": await OpenDeviceSessionAsync(JsonUtil.GetString(data, "id", "")); break;
                    case "selectSession": ShowMultiSession(JsonUtil.GetString(data, "ip", "")); break;
                    case "closeSession": if (_multiSession != null) _multiSession.ClosePeer(JsonUtil.GetString(data, "ip", "")); break;
                    case "saveDevice":
                        DeviceProfile savedDevice = _deviceLibrary.Upsert(JsonUtil.GetString(data, "id", ""), JsonUtil.GetString(data, "name", ""), JsonUtil.GetString(data, "ip", ""), JsonUtil.GetString(data, "group", "未分類"), JsonUtil.GetString(data, "tags", ""), JsonUtil.GetBool(data, "favorite", false));
                        _manager.TrustPeer(savedDevice.Ip); break;
                    case "updateDeviceMetadata":
                        _deviceLibrary.UpdateMetadata(JsonUtil.GetString(data, "id", ""), JsonUtil.GetString(data, "name", ""), JsonUtil.GetString(data, "group", "未分類"), JsonUtil.GetString(data, "tags", ""), JsonUtil.GetBool(data, "favorite", false)); break;
                    case "deleteDevice": _deviceLibrary.RemoveDevice(JsonUtil.GetString(data, "id", "")); break;
                    case "toggleFavorite": _deviceLibrary.ToggleFavorite(JsonUtil.GetString(data, "id", "")); break;
                    case "addGroup": _deviceLibrary.AddGroup(JsonUtil.GetString(data, "name", "")); break;
                    case "deleteGroup": _deviceLibrary.RemoveGroup(JsonUtil.GetString(data, "name", "")); break;
                    case "onlineRestart":
                        RequireOnline(); _ngrok.Start(_manager.Port, _config.NgrokDomain); break;
                    case "onlineStop":
                        RequireOnline(); _ngrok.Stop(); break;
                    case "onlineCopyCode":
                        RequireOnline();
                        string localCode = CreateOnlineCode();
                        if (localCode.Length == 0) throw new InvalidOperationException("ngrok 尚未取得公開網址，請稍候再試。");
                        Clipboard.SetText(localCode); Toast("已複製 Online 連線碼。"); break;
                    case "onlineConnect":
                        RequireOnline();
                        OnlinePeerCode onlinePeer = ParseOnlineCode(JsonUtil.GetString(data, "code", ""));
                        _config.LastOnlineEndpoint = onlinePeer.Endpoint;
                        _config.LastOnlinePeerKey = "";
                        _config.Save();
                        if (string.IsNullOrWhiteSpace(onlinePeer.RegistryUrl))
                            _deviceLibrary.UpsertOnline("", onlinePeer.Name, onlinePeer.Endpoint, onlinePeer.AccessKey, "未分類", "Online", false);
                        else
                            _deviceLibrary.UpsertOnline("", onlinePeer.Name, onlinePeer.Endpoint, onlinePeer.AccessKey, "未分類", "Online", false, onlinePeer.RegistryUrl, onlinePeer.RegistryDeviceId, onlinePeer.RegistrySecret);
                        OpenOnlineSession(onlinePeer.Endpoint, onlinePeer.AccessKey, onlinePeer.Name); break;
                    case "cloudflareSave":
                        RequireCloudflare();
                        SaveCloudflareSettings(JsonUtil.GetString(data, "hostname", ""), JsonUtil.GetString(data, "token", ""));
                        Toast("Cloudflare 設定已安全保存。"); break;
                    case "cloudflareStart":
                        RequireCloudflare();
                        SaveCloudflareSettings(JsonUtil.GetString(data, "hostname", ""), JsonUtil.GetString(data, "token", ""));
                        _cloudflare.StartHost(_cloudflareSettings.TunnelToken); break;
                    case "cloudflareStop":
                        RequireCloudflare(); _cloudflare.StopHost(); break;
                    case "cloudflareStopClients":
                        RequireCloudflare(); _cloudflare.StopClients(); Toast("已停止所有 Cloudflare Access 轉接。"); break;
                    case "cloudflareCopyCode":
                        RequireCloudflare();
                        string cloudflareCode = CreateCloudflareCode();
                        if (cloudflareCode.Length == 0) throw new InvalidOperationException("請先保存主機名稱，並確認 Cloudflare Tunnel 已連線。");
                        Clipboard.SetText(cloudflareCode); Toast("已複製 Cloudflare 遠端連線碼。"); break;
                    case "cloudflareConnect":
                        RequireCloudflare();
                        CloudflarePeerCode cloudflarePeer = ParseCloudflareCode(JsonUtil.GetString(data, "code", ""));
                        int localPort = cloudflarePeer.Relay
                            ? _cloudflareRelay.StartClient(cloudflarePeer.Hostname, cloudflarePeer.RoomId, cloudflarePeer.Secret)
                            : _cloudflare.StartAccess(cloudflarePeer.Hostname);
                        OpenOnlineSession("http://127.0.0.1:" + localPort, "", cloudflarePeer.Name); break;
                    case "cloudflareRelaySave":
                        RequireCloudflare();
                        SaveRelayHostname(JsonUtil.GetString(data, "hostname", ""));
                        Toast("HTTPS 中繼設定已保存；連線碼金鑰由 Windows DPAPI 加密保存。"); break;
                    case "cloudflareRelayStart":
                        RequireCloudflare();
                        SaveRelayHostname(JsonUtil.GetString(data, "hostname", ""));
                        _cloudflareRelay.StartHost(_cloudflareSettings.RelayHostname, _cloudflareSettings.RelayRoomId, _cloudflareSettings.RelaySecret); break;
                    case "cloudflareRelayStop":
                        RequireCloudflare(); _cloudflareRelay.StopHost(); break;
                    case "cloudflareRelayStopClients":
                        RequireCloudflare(); _cloudflareRelay.StopClients(); Toast("已停止本機的 HTTPS 中繼連線。"); break;
                    case "cloudflareRelayCopyCode":
                        RequireCloudflare();
                        string relayCode = CreateCloudflareRelayCode();
                        if (relayCode.Length == 0) throw new InvalidOperationException("請先保存中繼主機名稱，並啟動 HTTPS 中繼主機。");
                        Clipboard.SetText(relayCode); Toast("已複製 VPN 443 中繼連線碼。請只分享給可信任的遠端使用者。"); break;
                    case "copyIp":
                        string address = JsonUtil.GetString(data, "ip", "");
                        if (!NetworkUtils.LocalIPv4().Contains(address)) throw new InvalidOperationException("請選擇目前有效的本機位址。");
                        Clipboard.SetText(address); Toast("已複製本機 IP 位址。"); break;
                    case "allowControl": _manager.AllowRemoteControl = JsonUtil.GetBool(data, "enabled", false); break;
                    case "quality": _manager.Fps = JsonUtil.GetInt(data, "fps", 10); _manager.JpegQuality = JsonUtil.GetInt(data, "quality", 55); break;
                    case "sendFiles":
                        if (_transferring || !_manager.Connected) throw new InvalidOperationException("請先完成連線或等待目前傳輸結束。");
                        OpenFileDialog file = new OpenFileDialog { Multiselect = true, Title = "傳送檔案到遠端電腦" };
                        if (file.ShowDialog(this) == true) await SendPaths(file.FileNames); break;
                    case "sendFolder":
                        if (_transferring || !_manager.Connected) throw new InvalidOperationException("請先完成連線或等待目前傳輸結束。");
                        using (System.Windows.Forms.FolderBrowserDialog folder = new System.Windows.Forms.FolderBrowserDialog()) {
                            folder.Description = "選擇要傳送的資料夾";
                            if (folder.ShowDialog() == System.Windows.Forms.DialogResult.OK) await SendPaths(new[] { folder.SelectedPath });
                        } break;
                    case "openReceived": Directory.CreateDirectory(_manager.ReceiveDirectory); Process.Start("explorer.exe", "\"" + _manager.ReceiveDirectory + "\""); break;
                }
                PublishState();
            }
            catch (Exception ex) { Post(new { type = command == "connect" ? "connectError" : "toast", message = ex.Message }); }
        }

        private async Task SendPaths(string[] paths)
        {
            _transferring = true; _transfer = "準備傳送…"; PublishState();
            try { await Task.Run(delegate { _manager.SendPaths(paths); }); _transfer = "已交付傳輸；直接傳送已完成，回拉項目等待對方領取。"; }
            catch (Exception ex) { _transfer = "傳送失敗：" + ex.Message; }
            finally { _transferring = false; PublishState(); }
        }

        private void OpenSession()
        {
            string ip = string.IsNullOrWhiteSpace(_manager.ActivePeerIp) ? _manager.LastPeerIp : _manager.ActivePeerIp;
            if (string.IsNullOrWhiteSpace(ip)) { Toast("尚未選擇遠端電腦。"); return; }
            DeviceProfile device = _deviceLibrary.FindByIp(ip);
            OpenMultiSession(ip, device == null ? (_manager.RemoteInfo == null ? ip : _manager.RemoteInfo.Machine) : device.Name);
        }

        private async Task OpenDeviceSessionAsync(string id)
        {
            DeviceProfile device = _deviceLibrary.FindById((id ?? "").Trim());
            if (device == null) throw new InvalidOperationException("找不到這台裝置，請先加入裝置中心。");
            if (string.Equals(device.ConnectionMode, "online", StringComparison.OrdinalIgnoreCase))
            {
                OnlineDeviceCredential credential = _deviceLibrary.GetOnlineCredential(device.Id);
                if (credential == null) throw new InvalidOperationException("這台 Online 裝置的連線憑證無法讀取，請重新貼一次 CKO2 配對碼。");
                string endpoint = credential.Endpoint;
                string displayName = credential.Name;
                if (_registryClient != null && !string.IsNullOrWhiteSpace(credential.RegistryUrl) && !string.IsNullOrWhiteSpace(credential.RegistryDeviceId) && !string.IsNullOrWhiteSpace(credential.RegistrySecret))
                {
                    OnlineRegistryResult resolved = await Task.Run(delegate { return _registryClient.Resolve(credential.RegistryUrl, credential.RegistryDeviceId, credential.RegistrySecret); });
                    if (resolved.Ok)
                    {
                        if (!resolved.Online) throw new InvalidOperationException("這台 Online 裝置目前沒有回報有效端點，可能尚未開啟 KVM Online。");
                        if (!string.IsNullOrWhiteSpace(resolved.Endpoint))
                        {
                            endpoint = resolved.Endpoint;
                            if (!string.IsNullOrWhiteSpace(resolved.Name)) displayName = resolved.Name;
                            _deviceLibrary.UpdateOnlineEndpoint(device.Id, endpoint, displayName);
                        }
                    }
                    else
                    {
                        Log("Online registry resolve 失敗，改用最後端點：" + resolved.Error);
                    }
                }
                OpenOnlineSession(endpoint, credential.AccessKey, displayName);
                return;
            }
            OpenMultiSession(device.Ip, device.Name);
        }

        private void OpenMultiSession(string ip, string name)
        {
            EnsureMultiSessionWindow();
            _multiSession.OpenPeer(ip, name, _config.Fps);
            Hide();
        }

        private void OpenOnlineSession(string endpoint, string accessKey, string name)
        {
            EnsureMultiSessionWindow();
            _multiSession.OpenEndpoint(endpoint, accessKey, string.IsNullOrWhiteSpace(name) ? "Online 遠端" : name, _config.Fps);
            Hide();
        }

        private void EnsureMultiSessionWindow()
        {
            if (_multiSession != null) return;
            _multiSession = new MultiSessionWindow(_manager);
            _multiSession.ToolsRequested += delegate(string page, string peerId) {
                if (!string.Equals(page, "devices", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(peerId) && NetworkUtils.IsPrivateOrLinkLocal(peerId) && !string.Equals(_manager.ActivePeerIp, peerId, StringComparison.OrdinalIgnoreCase)) _manager.SetActivePeer(peerId);
                Show(); Activate(); Post(new { type = "page", name = page });
            };
            _multiSession.SessionsChanged += PublishState;
            _multiSession.Closed += delegate {
                _multiSession = null;
                // Remote desktop hides the main window. Closing the viewer must
                // restore it, otherwise the HTTP 8001 server looks like a ghost
                // process and a second launch collides with the original one.
                if (!_closed && !IsVisible)
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Show();
                    Activate();
                }
                PublishState();
            };
        }

        private void ShowMultiSession(string ip)
        {
            if (_multiSession == null) return;
            if (!string.IsNullOrWhiteSpace(ip)) _multiSession.SelectPeer(ip);
            if (_multiSession.WindowState == WindowState.Minimized) _multiSession.WindowState = WindowState.Normal;
            _multiSession.Show();
            _multiSession.Activate();
            Hide();
        }

        private void PublishState()
        {
            if (!_webReady || _closed) return;
            ScreenInfo info = _manager.RemoteInfo;
            Post(new { type = "state", state = new {
                machine = Environment.MachineName, localIps = NetworkUtils.LocalIPv4().ToArray(),
                localInterfaces = NetworkUtils.LocalNetworkInterfaces().ToArray(),
                nearbyPeers = _lanDiscovery == null ? new object[0] : _lanDiscovery.Snapshot(),
                discoveryRunning = _lanDiscovery != null && _lanDiscovery.Running,
                peer = string.IsNullOrWhiteSpace(_manager.ActivePeerIp) ? _manager.LastPeerIp : _manager.ActivePeerIp,
                peerName = _manager.Connected && info != null ? info.Machine : "",
                connected = _manager.Connected, running = _manager.Running, allowControl = _manager.AllowRemoteControl,
                activePeerConnected = _manager.IsPeerConnected(_manager.ActivePeerIp),
                fps = _manager.Fps, quality = _manager.JpegQuality, receiveDirectory = _manager.ReceiveDirectory,
                route = (_manager.OutboundConnected ? "主動連線" : _manager.InboundSeen ? "由對方連入" : "等待連線") + " · HTTP " + _manager.Port,
                agentApi = "127.0.0.1:" + _agentApi.Port, transferStatus = _transfer, transferring = _transferring, error = _error,
                library = _deviceLibrary.Snapshot(), sessions = _multiSession == null ? new object[0] : _multiSession.Snapshot(),
                onlineMode = BuildFlavor.IsOnline,
                online = new {
                    running = _ngrok != null && _ngrok.Running,
                    publicUrl = _ngrok == null ? "" : _ngrok.PublicUrl,
                    detail = _ngrok == null ? "" : _ngrok.Detail + (string.IsNullOrWhiteSpace(_registryDetail) ? "" : " · " + _registryDetail),
                    pairingCode = BuildFlavor.IsOnline ? CreateOnlineCode() : "",
                    listenPort = _manager.Port,
                    fixedDomain = _config.NgrokDomain ?? ""
                },
                cloudflareMode = BuildFlavor.IsCloudflare,
                cloudflare = new {
                    running = _cloudflare != null && _cloudflare.Running,
                    connected = _cloudflare != null && _cloudflare.Connected,
                    detail = _cloudflare == null ? "" : _cloudflare.Detail,
                    hostname = _cloudflareSettings == null ? "" : _cloudflareSettings.Hostname,
                    tokenConfigured = _cloudflareSettings != null && !string.IsNullOrWhiteSpace(_cloudflareSettings.TunnelToken),
                    pairingCode = BuildFlavor.IsCloudflare ? CreateCloudflareCode() : "",
                    clients = _cloudflare == null ? 0 : _cloudflare.ClientCount,
                    listenPort = _manager.Port
                },
                cloudflareRelay = new {
                    running = _cloudflareRelay != null && _cloudflareRelay.HostRunning,
                    connected = _cloudflareRelay != null && _cloudflareRelay.HostConnected,
                    detail = _cloudflareRelay == null ? "" : _cloudflareRelay.Detail,
                    hostname = _cloudflareSettings == null ? "" : _cloudflareSettings.RelayHostname,
                    pairingCode = BuildFlavor.IsCloudflare ? CreateCloudflareRelayCode() : "",
                    clients = _cloudflareRelay == null ? 0 : _cloudflareRelay.ClientCount
                }
            }});
        }

        private void RequireCloudflare()
        {
            if (!BuildFlavor.IsCloudflare || _cloudflare == null || _cloudflareSettings == null)
                throw new InvalidOperationException("這個功能只在 Recovery Cloudflare 版提供。");
        }

        private void SaveCloudflareSettings(string hostname, string token)
        {
            hostname = CloudflareTunnelService.NormalizeHostname(hostname);
            if (string.IsNullOrWhiteSpace(token)) token = _cloudflareSettings.TunnelToken;
            _cloudflareSettings.Hostname = hostname;
            _cloudflareSettings.TunnelToken = token;
            CloudflareSettingsStore.Save(_cloudflareSettings);
        }

        private void SaveRelayHostname(string hostname)
        {
            hostname = CloudflareTunnelService.NormalizeHostname(hostname);
            _cloudflareSettings.RelayHostname = hostname;
            CloudflareSettingsStore.Save(_cloudflareSettings);
        }

        private string CreateCloudflareCode()
        {
            if (!BuildFlavor.IsCloudflare || _cloudflareSettings == null || string.IsNullOrWhiteSpace(_cloudflareSettings.Hostname) || _cloudflare == null || !_cloudflare.Connected) return "";
            string json = JsonUtil.Serialize(new { v = 1, host = _cloudflareSettings.Hostname, name = Environment.MachineName });
            return "CCF1." + Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(json));
        }

        private static CloudflarePeerCode ParseCloudflareCode(string code)
        {
            code = (code ?? "").Trim();
            if (code.StartsWith("CCF2.", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Dictionary<string, object> relay = JsonUtil.Parse(System.Text.Encoding.UTF8.GetString(Base64UrlDecode(code.Substring(5))));
                    string room = JsonUtil.GetString(relay, "room", "").ToLowerInvariant();
                    string secret = JsonUtil.GetString(relay, "key", "");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(room, "^[a-f0-9]{32}$") || !System.Text.RegularExpressions.Regex.IsMatch(secret, "^[A-Za-z0-9_-]{40,80}$"))
                        throw new InvalidOperationException("Cloudflare HTTPS 中繼連線碼缺少有效的配對資料。");
                    return new CloudflarePeerCode
                    {
                        Relay = true,
                        Hostname = CloudflareTunnelService.NormalizeHostname(JsonUtil.GetString(relay, "host", "")),
                        RoomId = room,
                        Secret = secret,
                        Name = JsonUtil.GetString(relay, "name", "Cloudflare 遠端").Trim()
                    };
                }
                catch (InvalidOperationException) { throw; }
                catch { throw new InvalidOperationException("Cloudflare HTTPS 中繼連線碼格式不正確。"); }
            }
            if (!code.StartsWith("CCF1.", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("請貼上以 CCF1. 開頭的 Cloudflare 遠端連線碼。");
            try
            {
                Dictionary<string, object> data = JsonUtil.Parse(System.Text.Encoding.UTF8.GetString(Base64UrlDecode(code.Substring(5))));
                return new CloudflarePeerCode
                {
                    Hostname = CloudflareTunnelService.NormalizeHostname(JsonUtil.GetString(data, "host", "")),
                    Name = JsonUtil.GetString(data, "name", "Cloudflare 遠端").Trim()
                };
            }
            catch (InvalidOperationException) { throw; }
            catch { throw new InvalidOperationException("Cloudflare 連線碼格式不正確。"); }
        }

        private sealed class CloudflarePeerCode
        {
            public string Hostname;
            public string Name;
            public bool Relay;
            public string RoomId;
            public string Secret;
        }

        private string CreateCloudflareRelayCode()
        {
            if (!BuildFlavor.IsCloudflare || _cloudflareSettings == null || _cloudflareRelay == null || !_cloudflareRelay.HostConnected) return "";
            string json = JsonUtil.Serialize(new { v = 2, host = _cloudflareSettings.RelayHostname, room = _cloudflareSettings.RelayRoomId, key = _cloudflareSettings.RelaySecret, name = Environment.MachineName });
            return "CCF2." + Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(json));
        }

        private void TryPublishRegistry(bool force)
        {
            if (!BuildFlavor.IsOnline || _registryClient == null || _ngrok == null || string.IsNullOrWhiteSpace(_ngrok.PublicUrl)) return;
            DateTime now = DateTime.UtcNow;
            if (!force && now < _nextRegistryPublishUtc) return;
            if (Interlocked.CompareExchange(ref _registryPublishing, 1, 0) != 0) return;
            _nextRegistryPublishUtc = now.AddSeconds(35);
            string registryUrl = _config.OnlineRegistryUrl;
            string deviceId = _config.OnlineDeviceId;
            string secret = _config.OnlineRegistrySecret;
            string endpoint = _ngrok.PublicUrl;
            Task.Run(delegate
            {
                try
                {
                    _registryClient.Register(registryUrl, deviceId, secret, endpoint, Environment.MachineName);
                    OnUi(delegate { _registryDetail = "裝置目錄已同步"; PublishState(); });
                }
                catch (Exception ex)
                {
                    OnUi(delegate { _registryDetail = "裝置目錄同步失敗"; Log("Online registry register 失敗：" + ex.Message); PublishState(); });
                }
                finally { Interlocked.Exchange(ref _registryPublishing, 0); }
            });
        }

        private void RequireOnline()
        {
            if (!BuildFlavor.IsOnline || _ngrok == null) throw new InvalidOperationException("這個功能只在 ChaoChao KVM Online 版提供。");
        }

        private string CreateOnlineCode()
        {
            if (!BuildFlavor.IsOnline || _ngrok == null || string.IsNullOrWhiteSpace(_ngrok.PublicUrl)) return "";
            string json = JsonUtil.Serialize(new {
                v = 2,
                url = _ngrok.PublicUrl,
                key = _config.OnlineAccessKey,
                name = Environment.MachineName,
                registry = _config.OnlineRegistryUrl,
                device_id = _config.OnlineDeviceId,
                registry_secret = _config.OnlineRegistrySecret
            });
            return "CKO2." + Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(json));
        }

        private static OnlinePeerCode ParseOnlineCode(string code)
        {
            code = (code ?? "").Trim();
            bool v2 = code.StartsWith("CKO2.", StringComparison.OrdinalIgnoreCase);
            bool v1 = code.StartsWith("CKO1.", StringComparison.OrdinalIgnoreCase);
            if (!v1 && !v2) throw new InvalidOperationException("不是有效的 ChaoChao KVM Online 連線碼。");
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(code.Substring(5)));
                Dictionary<string, object> data = JsonUtil.Parse(json);
                string endpoint = JsonUtil.GetString(data, "url", "").Trim().TrimEnd('/');
                string key = JsonUtil.GetString(data, "key", "").Trim();
                string name = JsonUtil.GetString(data, "name", "Online 遠端").Trim();
                Uri uri;
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) || !string.Equals(uri.Scheme, "tcp", StringComparison.OrdinalIgnoreCase) || uri.Port <= 0 || key.Length < 16) throw new InvalidOperationException();
                OnlinePeerCode result = new OnlinePeerCode { Endpoint = "tcp://" + uri.Host + ":" + uri.Port, AccessKey = key, Name = name };
                if (v2)
                {
                    result.RegistryUrl = JsonUtil.GetString(data, "registry", "").Trim().TrimEnd('/');
                    result.RegistryDeviceId = JsonUtil.GetString(data, "device_id", "").Trim();
                    result.RegistrySecret = JsonUtil.GetString(data, "registry_secret", "").Trim();
                    Uri registryUri;
                    if (!Uri.TryCreate(result.RegistryUrl, UriKind.Absolute, out registryUri) || !string.Equals(registryUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) || result.RegistryDeviceId.Length < 16 || result.RegistrySecret.Length < 24) throw new InvalidOperationException();
                    result.RegistryUrl = registryUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
                }
                return result;
            }
            catch (InvalidOperationException) { throw; }
            catch { throw new InvalidOperationException("Online 連線碼格式不正確。"); }
        }

        private static string Base64UrlEncode(byte[] data) { return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        private static byte[] Base64UrlDecode(string text)
        {
            string value = (text ?? "").Replace('-', '+').Replace('_', '/');
            while (value.Length % 4 != 0) value += "=";
            return Convert.FromBase64String(value);
        }

        private sealed class OnlinePeerCode
        {
            public string Endpoint;
            public string AccessKey;
            public string Name;
            public string RegistryUrl;
            public string RegistryDeviceId;
            public string RegistrySecret;
        }
        private void Log(string text) { OnUi(delegate { _logs.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text); while (_logs.Count > 100) _logs.Dequeue(); PublishLogs(); }); }
        private void PublishLogs() { Post(new { type = "logs", text = string.Join(Environment.NewLine, _logs.ToArray()) }); }
        private void Toast(string message) { Post(new { type = "toast", message = message }); }
        private void Post(object message) { if (_webReady && !_closed && Web.CoreWebView2 != null) Web.CoreWebView2.PostWebMessageAsJson(JsonUtil.Serialize(message)); }
        private void OnUi(Action action) { if (_closed || Dispatcher.HasShutdownStarted) return; Dispatcher.BeginInvoke(new Action(delegate { if (!_closed) action(); })); }
    }
}
