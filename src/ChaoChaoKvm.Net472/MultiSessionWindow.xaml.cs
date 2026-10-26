using ChaoChaoKvm.Core;
using ChaoChaoKvm.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChaoChaoKvm
{
    public partial class MultiSessionWindow : Window
    {
        private readonly KvmPeerManager _manager;
        private readonly Dictionary<string, RemotePeerSession> _sessions = new Dictionary<string, RemotePeerSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RemoteInputQueue> _queues = new Dictionary<string, RemoteInputQueue>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _renderTimer;
        private readonly HashSet<int> _keys = new HashSet<int>();
        private readonly HashSet<int> _buttons = new HashSet<int>();
        private RemotePeerSession _active;
        private byte[] _latestFrame;
        private int _width = 1, _height = 1;
        private bool _hasFrame, _inputPaused, _readOnly, _fullscreen;
        private DateTime _lastMove = DateTime.MinValue, _lastFrame = DateTime.MinValue;
        private Point _lastPoint;
        private WindowState _previousState;
        // UpdateChrome runs every render tick; reuse brushes instead of
        // allocating new GDI/WPF resources dozens of times per second.
        private static readonly Brush ControlReadyBrush = new SolidColorBrush(Color.FromRgb(28, 105, 182));
        private static readonly Brush ControlPausedBrush = new SolidColorBrush(Color.FromRgb(161, 96, 34));
        private static readonly Brush PeerOnlineBrush = new SolidColorBrush(Color.FromRgb(35, 157, 105));
        private static readonly Brush PeerConnectingBrush = new SolidColorBrush(Color.FromRgb(206, 149, 49));
        private static readonly Brush PeerIdleBrush = new SolidColorBrush(Color.FromRgb(155, 169, 185));

        public event Action<string, string> ToolsRequested;
        public event Action SessionsChanged;

        public MultiSessionWindow(KvmPeerManager manager)
        {
            _manager = manager;
            InitializeComponent();
            Title = BuildFlavor.IsOnline ? "多重遠端桌面 · 吵吵 KVM Online" : "多重遠端桌面 · 吵吵 KVM";
            _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _renderTimer.Tick += Render;
            _renderTimer.Start();
            Deactivated += delegate { ReleaseRemoteInput(); };
            Viewer.LostMouseCapture += delegate { if (_buttons.Count > 0) ReleaseRemoteInput(); };
            Closed += delegate { DisposeSessions(); _renderTimer.Stop(); };
        }

        public void OpenPeer(string ip, string displayName, int activeFps)
        {
            if (string.IsNullOrWhiteSpace(ip)) return;
            RemotePeerSession session;
            if (!_sessions.TryGetValue(ip, out session))
            {
                session = new RemotePeerSession(_manager, ip, displayName, activeFps);
                AttachSession(ip, session);
            }
            else if (!string.IsNullOrWhiteSpace(displayName)) session.DisplayName = displayName;
            SelectPeer(ip);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            Activate();
            RaiseSessionsChanged();
        }

        public void OpenEndpoint(string endpoint, string accessKey, string displayName, int activeFps)
        {
            endpoint = (endpoint ?? "").Trim().TrimEnd('/');
            if (endpoint.Length == 0) return;
            RemotePeerSession session;
            if (!_sessions.TryGetValue(endpoint, out session))
            {
                session = new RemotePeerSession(endpoint, accessKey, displayName, activeFps);
                AttachSession(endpoint, session);
            }
            else if (!string.IsNullOrWhiteSpace(displayName)) session.DisplayName = displayName;
            SelectPeer(endpoint);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            Activate();
            RaiseSessionsChanged();
        }

        private void AttachSession(string id, RemotePeerSession session)
        {
            session.FrameAvailable += delegate(byte[] frame) { if (ReferenceEquals(_active, session)) Interlocked.Exchange(ref _latestFrame, frame); };
            session.RemoteInfoChanged += delegate { OnUi(delegate { RebuildTabs(); UpdateChrome(); }); };
            session.StatusChanged += delegate { OnUi(delegate { RebuildTabs(); UpdateChrome(); RaiseSessionsChanged(); }); };
            _sessions[id] = session;
            _queues[id] = new RemoteInputQueue(session.SendInput, delegate { return session.Connected && _sessions.ContainsKey(id); });
        }

        public void SelectPeer(string ip)
        {
            RemotePeerSession next;
            if (!_sessions.TryGetValue(ip, out next) || ReferenceEquals(_active, next)) return;
            ReleaseRemoteInput();
            if (_active != null) _active.SetForeground(false);
            _active = next;
            _active.SetForeground(true);
            _inputPaused = false; _hasFrame = false; RemoteImage.Source = null;
            Interlocked.Exchange(ref _latestFrame, _active.LatestFrame);
            RebuildTabs(); UpdateChrome(); Viewer.Focus(); RaiseSessionsChanged();
        }

        public void ClosePeer(string ip)
        {
            RemotePeerSession session;
            if (!_sessions.TryGetValue(ip, out session)) return;
            bool active = ReferenceEquals(_active, session);
            if (active) ReleaseRemoteInput();
            RemoteInputQueue queue;
            if (_queues.TryGetValue(ip, out queue)) { queue.Complete(); _queues.Remove(ip); }
            _sessions.Remove(ip); session.Dispose();
            if (active)
            {
                _active = null;
                RemotePeerSession next = _sessions.Values.FirstOrDefault();
                if (next != null) SelectPeer(next.PeerIp); else { RemoteImage.Source = null; _hasFrame = false; }
            }
            RebuildTabs(); UpdateChrome(); RaiseSessionsChanged();
            // The final tab closes the viewer and returns to the main window.
            // The app (and its sharing server) stays alive until the main
            // window is explicitly closed.
            if (_sessions.Count == 0) Close();
        }

        public object Snapshot()
        {
            return _sessions.Values.Select(s => s.Snapshot(ReferenceEquals(s, _active))).ToArray();
        }

        private void RebuildTabs()
        {
            SessionTabsPanel.Children.Clear();
            foreach (RemotePeerSession session in _sessions.Values)
            {
                StackPanel shell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 0, 1, 0) };
                Button select = new Button { Style = (Style)FindResource("TabButton"), Tag = session.PeerIp, MinWidth = 90 };
                StackPanel label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(session.Connected ? Color.FromRgb(38, 164, 103) : Color.FromRgb(160, 173, 187)) });
                label.Children.Add(new TextBlock { Text = session.DisplayName, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = ReferenceEquals(session, _active) ? FontWeights.SemiBold : FontWeights.Normal });
                select.Content = label;
                if (ReferenceEquals(session, _active)) { select.Background = new SolidColorBrush(Color.FromRgb(225, 238, 252)); select.BorderBrush = new SolidColorBrush(Color.FromRgb(185, 212, 242)); select.Foreground = new SolidColorBrush(Color.FromRgb(22, 96, 172)); }
                select.Click += delegate(object sender, RoutedEventArgs e) { SelectPeer((string)((Button)sender).Tag); };
                Button close = new Button { Content = "×", Tag = session.PeerIp, Width = 22, Height = 30, Margin = new Thickness(-4, 0, 3, 0), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(102, 121, 140)), FontSize = 15, Cursor = Cursors.Hand, ToolTip = "關閉此遠端分頁" };
                close.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; ClosePeer((string)((Button)sender).Tag); };
                shell.Children.Add(select); shell.Children.Add(close); SessionTabsPanel.Children.Add(shell);
            }
        }

        private void Render(object sender, EventArgs e)
        {
            byte[] frame = Interlocked.Exchange(ref _latestFrame, null);
            if (_active != null && frame != null)
            {
                try
                {
                    BitmapImage image = new BitmapImage();
                    using (MemoryStream stream = new MemoryStream(frame)) { image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); }
                    RemoteImage.Source = image; _width = image.PixelWidth; _height = image.PixelHeight; _hasFrame = true; _lastFrame = DateTime.UtcNow;
                }
                catch { }
            }
            bool fresh = _active != null && _active.Connected && _hasFrame && (DateTime.UtcNow - _lastFrame).TotalSeconds < 5;
            EmptyPanel.Visibility = fresh ? Visibility.Collapsed : Visibility.Visible;
            if (_active == null) { EmptyTitle.Text = "選擇一台遠端電腦"; EmptyDetail.Text = "從裝置中心開啟電腦後，工作階段會出現在上方分頁。"; }
            else if (!_active.Connected) { EmptyTitle.Text = "正在連線 " + _active.DisplayName; EmptyDetail.Text = "連線尚未完成 · 背景會持續重試"; }
            else if (!fresh) { EmptyTitle.Text = "正在等待畫面"; EmptyDetail.Text = _active.DisplayName + " 已連線，等待第一張遠端畫面。"; }
            UpdateChrome();
        }

        private void UpdateChrome()
        {
            bool available = _active != null;
            RetryButton.IsEnabled = EndSessionButton.IsEnabled = available;
            ControlButton.IsEnabled = available;
            ControlButton.Content = _readOnly ? "唯讀中" : _inputPaused ? "已暫停" : "控制中";
            ControlButton.Foreground = _readOnly || _inputPaused ? ControlPausedBrush : ControlReadyBrush;
            ControlButton.ToolTip = _readOnly ? "目前為唯讀模式。點此允許本機傳送滑鼠、鍵盤事件。" :
                _inputPaused ? "遠端輸入已暫停。點此恢復操作。" : "點此切換唯讀模式，停止傳送滑鼠與鍵盤事件（Ctrl + Alt + Esc 暫停輸入）";
            if (!available)
            {
                PeerName.Text = "遠端桌面";
                SessionStatus.Text = _sessions.Count + " 個工作階段";
                StatusDot.Fill = PeerIdleBrush;
                return;
            }
            PeerName.Text = _active.DisplayName;
            SessionStatus.Text = _active.Connected ? "已連線" : "連線中";
            StatusDot.Fill = _active.Connected ? PeerOnlineBrush : PeerConnectingBrush;
        }

        private RemoteInputQueue ActiveQueue { get { RemoteInputQueue q; return _active != null && _queues.TryGetValue(_active.PeerIp, out q) ? q : null; } }

        private void ReleaseRemoteInput()
        {
            RemoteInputQueue queue = ActiveQueue;
            if (queue != null)
            {
                foreach (int vk in _keys) queue.Enqueue(new Dictionary<string, object> { { "kind", "key" }, { "vk", vk }, { "down", false } });
                foreach (int button in _buttons) { Dictionary<string, object> data = Pointer("button", _lastPoint); data["button"] = button; data["down"] = false; queue.Enqueue(data); }
            }
            _keys.Clear(); _buttons.Clear(); if (Viewer.IsMouseCaptured) Viewer.ReleaseMouseCapture();
        }

        private bool CanPointerInput { get { return !_readOnly && !_inputPaused && _active != null && _active.Connected && _hasFrame; } }
        private bool CanKeyboardInput { get { return CanPointerInput && Viewer.IsKeyboardFocusWithin; } }
        private bool Normalize(Point p, bool clamp, out Point normalized) { return ViewerGeometry.Normalize(p, Viewer.ActualWidth, Viewer.ActualHeight, _width, _height, clamp, out normalized); }

        private void MovePointer(object sender, MouseEventArgs e)
        {
            if (!CanPointerInput || (DateTime.UtcNow - _lastMove).TotalMilliseconds < 12) return;
            Point p; if (!Normalize(e.GetPosition(Viewer), _buttons.Count > 0, out p)) return;
            _lastMove = DateTime.UtcNow; _lastPoint = p; RemoteInputQueue q = ActiveQueue; if (q != null) q.Enqueue(Pointer("move", p));
        }

        private void DownPointer(object sender, MouseButtonEventArgs e)
        {
            if (_readOnly || _active == null || !_active.Connected || !_hasFrame) return;
            if (_inputPaused) { _inputPaused = false; UpdateChrome(); }
            Viewer.Focus();
            Point p; if (!Normalize(e.GetPosition(Viewer), false, out p)) return;
            int button = Button(e.ChangedButton); if (button == 0) return;
            _lastPoint = p; _buttons.Add(button); Viewer.CaptureMouse(); Dictionary<string, object> data = Pointer("button", p); data["button"] = button; data["down"] = true; RemoteInputQueue q = ActiveQueue; if (q != null) q.Enqueue(data); e.Handled = true;
        }

        private void UpPointer(object sender, MouseButtonEventArgs e)
        {
            int button = Button(e.ChangedButton); if (!_buttons.Remove(button)) return;
            Point p; if (!Normalize(e.GetPosition(Viewer), true, out p)) p = _lastPoint;
            _lastPoint = p; Dictionary<string, object> data = Pointer("button", p); data["button"] = button; data["down"] = false; RemoteInputQueue q = ActiveQueue; if (q != null) q.Enqueue(data); if (_buttons.Count == 0) Viewer.ReleaseMouseCapture(); e.Handled = true;
        }

        private void WheelPointer(object sender, MouseWheelEventArgs e)
        {
            if (!CanPointerInput) return; Point p; if (!Normalize(e.GetPosition(Viewer), false, out p)) return;
            Dictionary<string, object> data = Pointer("wheel", p); data["delta"] = e.Delta; RemoteInputQueue q = ActiveQueue; if (q != null) q.Enqueue(data); e.Handled = true;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt)) { ReleaseRemoteInput(); _inputPaused = true; UpdateChrome(); e.Handled = true; return; }
            if (!CanKeyboardInput) return; int vk = KeyInterop.VirtualKeyFromKey(key); if (vk <= 0) return; _keys.Add(vk); RemoteInputQueue q = ActiveQueue; if (q != null) q.Enqueue(new Dictionary<string, object> { { "kind", "key" }, { "vk", vk }, { "down", true } }); e.Handled = true;
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key); if (!_keys.Remove(vk)) return; RemoteInputQueue q = ActiveQueue; if (q != null) q.Enqueue(new Dictionary<string, object> { { "kind", "key" }, { "vk", vk }, { "down", false } }); e.Handled = true;
        }

        private void OpenDeviceCenter(object sender, RoutedEventArgs e) { RaiseTool("devices"); }
        private void OpenFiles(object sender, RoutedEventArgs e) { ReleaseRemoteInput(); RaiseTool("files"); }
        private void OpenSettings(object sender, RoutedEventArgs e) { ReleaseRemoteInput(); RaiseTool("settings"); }
        private void RaiseTool(string name) { if (ToolsRequested != null) ToolsRequested(name, _active == null ? "" : _active.PeerIp); }
        private void CloseView(object sender, RoutedEventArgs e) { Close(); }

        private void EndActiveSession(object sender, RoutedEventArgs e)
        {
            if (_active != null) ClosePeer(_active.PeerIp);
        }

        private void RetryActiveSession(object sender, RoutedEventArgs e)
        {
            if (_active == null) return;
            ReleaseRemoteInput();
            _active.Retry();
            if (_manager != null && string.Equals(_manager.ActivePeerIp, _active.PeerIp, StringComparison.OrdinalIgnoreCase))
                _manager.RetryNow();
            UpdateChrome();
        }

        private void ToggleReadOnly(object sender, RoutedEventArgs e)
        {
            if (_active == null) return;
            ReleaseRemoteInput();
            _readOnly = !_readOnly;
            if (!_readOnly) _inputPaused = false;
            UpdateChrome();
            if (!_readOnly) Viewer.Focus();
        }

        private void ToggleFullScreen(object sender, RoutedEventArgs e)
        {
            ReleaseRemoteInput(); if (!_fullscreen) { _previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized; } else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = _previousState; } _fullscreen = !_fullscreen; FullScreenButton.Content = _fullscreen ? "⛶ 視窗" : "⛶ 全螢幕"; FullScreenButton.ToolTip = _fullscreen ? "離開全螢幕" : "全螢幕"; Viewer.Focus();
        }

        private void DisposeSessions() { ReleaseRemoteInput(); foreach (RemoteInputQueue q in _queues.Values) q.Complete(); foreach (RemotePeerSession s in _sessions.Values) s.Dispose(); _queues.Clear(); _sessions.Clear(); _active = null; }
        private void RaiseSessionsChanged() { if (SessionsChanged != null) SessionsChanged(); }
        private void OnUi(Action action) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action); }
        private static int Button(MouseButton button) { return button == MouseButton.Left ? 1 : button == MouseButton.Right ? 2 : button == MouseButton.Middle ? 3 : 0; }
        private static Dictionary<string, object> Pointer(string kind, Point p) { return new Dictionary<string, object> { { "kind", kind }, { "x", p.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) }, { "y", p.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) } }; }
    }
}
