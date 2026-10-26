using ChaoChaoKvm.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChaoChaoKvm
{
    public partial class SessionWindow : Window
    {
        private readonly KvmPeerManager _manager;
        private readonly DispatcherTimer _renderTimer;
        private readonly RemoteInputQueue _input;
        private readonly HashSet<int> _keys = new HashSet<int>();
        private readonly HashSet<int> _buttons = new HashSet<int>();
        private byte[] _latestFrame;
        private int _width = 1, _height = 1;
        private bool _inputPaused, _hasFrame, _fullscreen, _toolbarCollapsed, _chromeEmphasized, _hintEmphasized;
        private DateTime _lastMove = DateTime.MinValue, _lastFrame = DateTime.MinValue;
        private Point _lastPoint;
        private WindowState _previousState;
        public event Action<string> ToolsRequested;

        public SessionWindow(KvmPeerManager manager)
        {
            InitializeComponent(); _manager = manager; _input = new RemoteInputQueue(manager);
            _manager.RemoteFrame += Frame;
            _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _renderTimer.Tick += Render; _renderTimer.Start();
            Deactivated += delegate { ReleaseRemoteInput(); SetChromeEmphasis(false); SetHintEmphasis(false); };
            Viewer.LostMouseCapture += delegate { if (_buttons.Count > 0) ReleaseRemoteInput(); };
            Closed += delegate { ReleaseRemoteInput(); _manager.RemoteFrame -= Frame; _renderTimer.Stop(); _input.Complete(); };
        }
        private void Frame(byte[] data) { Interlocked.Exchange(ref _latestFrame, data); }
        private void Render(object sender, EventArgs e)
        {
            byte[] frame = Interlocked.Exchange(ref _latestFrame, null);
            if (_manager.Connected && frame != null)
            {
                try {
                    BitmapImage image = new BitmapImage();
                    using (MemoryStream stream = new MemoryStream(frame)) { image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); }
                    RemoteImage.Source = image; _width = image.PixelWidth; _height = image.PixelHeight; _hasFrame = true; _lastFrame = DateTime.UtcNow;
                } catch { }
            }
            bool fresh = _manager.Connected && _hasFrame && (DateTime.UtcNow - _lastFrame).TotalSeconds < 4;
            if (!fresh) { ReleaseRemoteInput(); _inputPaused = false; RemoteImage.Source = null; _hasFrame = false; }
            EmptyPanel.Visibility = fresh ? Visibility.Collapsed : Visibility.Visible;
            EmptyTitle.Text = _manager.Connected ? "正在等待遠端畫面" : "連線暫時中斷";
            EmptyDetail.Text = _manager.Connected ? "畫面恢復後即可直接操作；目前不會傳送輸入。" : "正在背景重新連線。恢復後即可直接操作。";
            var info = _manager.RemoteInfo;
            PeerName.Text = info != null && !string.IsNullOrWhiteSpace(info.Machine) ? info.Machine : "遠端桌面";
            SessionStatus.Text = fresh ? "已連線 · " + _width + " × " + _height + (_inputPaused ? " · 輸入已暫停" : " · 直接操作") : "等待畫面";
            ModeHint.Text = _inputPaused ? "遠端輸入已暫停 · 點一下畫面即可立即繼續" : "直接操作模式 · 滑鼠移入畫面即可操作，點一下即可輸入鍵盤";
        }
        private void ReleaseRemoteInput()
        {
            foreach (int vk in _keys) _input.Enqueue(new Dictionary<string, object> { {"kind", "key"}, {"vk", vk}, {"down", false} });
            _keys.Clear();
            foreach (int button in _buttons) { var data = Pointer("button", _lastPoint); data["button"] = button; data["down"] = false; _input.Enqueue(data); }
            _buttons.Clear();
            if (Viewer.IsMouseCaptured) Viewer.ReleaseMouseCapture();
        }
        private void PauseRemoteInput()
        {
            ReleaseRemoteInput();
            _inputPaused = true;
            ModeHint.Text = "遠端輸入已暫停 · 點一下畫面即可立即繼續";
            AnimateOpacity(HintGlass, 0.96);
        }
        private bool CanPointerInput { get { return !_inputPaused && _hasFrame && _manager.Connected; } }
        private bool CanKeyboardInput { get { return CanPointerInput && Viewer.IsKeyboardFocusWithin; } }
        private bool Normalize(Point point, bool clamp, out Point normalized)
        {
            return ViewerGeometry.Normalize(point, Viewer.ActualWidth, Viewer.ActualHeight, _width, _height, clamp, out normalized);
        }
        private void MovePointer(object sender, MouseEventArgs e)
        {
            if (!CanPointerInput || (DateTime.UtcNow - _lastMove).TotalMilliseconds < 12) return;
            Point point; if (!Normalize(e.GetPosition(Viewer), _buttons.Count > 0, out point)) return;
            _lastMove = DateTime.UtcNow; _lastPoint = point; _input.Enqueue(Pointer("move", point));
        }
        private void DownPointer(object sender, MouseButtonEventArgs e)
        {
            if (!_hasFrame || !_manager.Connected) return;
            if (_inputPaused)
            {
                _inputPaused = false;
                if (!_hintEmphasized) AnimateOpacity(HintGlass, 0.42);
            }
            Viewer.Focus(); Point point; if (!Normalize(e.GetPosition(Viewer), false, out point)) return;
            int button = Button(e.ChangedButton); if (button == 0) return;
            _lastPoint = point; _buttons.Add(button); Viewer.CaptureMouse();
            var data = Pointer("button", point); data["button"] = button; data["down"] = true; _input.Enqueue(data); e.Handled = true;
        }
        private void UpPointer(object sender, MouseButtonEventArgs e)
        {
            int button = Button(e.ChangedButton); if (!_buttons.Remove(button)) return;
            Point point; if (!Normalize(e.GetPosition(Viewer), true, out point)) point = _lastPoint;
            _lastPoint = point; var data = Pointer("button", point); data["button"] = button; data["down"] = false; _input.Enqueue(data);
            if (_buttons.Count == 0) Viewer.ReleaseMouseCapture(); e.Handled = true;
        }
        private void WheelPointer(object sender, MouseWheelEventArgs e)
        {
            if (!CanPointerInput) return; Point point; if (!Normalize(e.GetPosition(Viewer), false, out point)) return;
            var data = Pointer("wheel", point); data["delta"] = e.Delta; _input.Enqueue(data); e.Handled = true;
        }
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt)) { PauseRemoteInput(); ToolbarToggleButton.Focus(); e.Handled = true; return; }
            if (!CanKeyboardInput) return; int vk = KeyInterop.VirtualKeyFromKey(key); if (vk <= 0) return;
            _keys.Add(vk); _input.Enqueue(new Dictionary<string, object> { {"kind", "key"}, {"vk", vk}, {"down", true} }); e.Handled = true;
        }
        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
            if (!_keys.Remove(vk)) return;
            _input.Enqueue(new Dictionary<string, object> { {"kind", "key"}, {"vk", vk}, {"down", false} }); e.Handled = true;
        }
        private void OpenFiles(object sender, RoutedEventArgs e) { ReleaseRemoteInput(); if (ToolsRequested != null) ToolsRequested("files"); }
        private void OpenSettings(object sender, RoutedEventArgs e) { ReleaseRemoteInput(); if (ToolsRequested != null) ToolsRequested("settings"); }
        private void CloseView(object sender, RoutedEventArgs e) { Close(); }
        private void ChromePointerActivity(object sender, MouseEventArgs e)
        {
            Point point = e.GetPosition(this);
            SetChromeEmphasis(point.Y <= 104);
            SetHintEmphasis(ActualHeight > 0 && point.Y >= ActualHeight - 72);
        }
        private void ChromePointerLeave(object sender, MouseEventArgs e)
        {
            SetChromeEmphasis(false);
            SetHintEmphasis(false);
        }
        private void SetChromeEmphasis(bool emphasized)
        {
            if (_chromeEmphasized == emphasized) return;
            _chromeEmphasized = emphasized;
            AnimateOpacity(ToolbarGlass, emphasized ? 1.0 : (_toolbarCollapsed ? 0.54 : 0.32));
            if (!_toolbarCollapsed) AnimateOpacity(StatusGlass, emphasized ? 0.96 : 0.28);
        }
        private void SetHintEmphasis(bool emphasized)
        {
            if (_hintEmphasized == emphasized) return;
            _hintEmphasized = emphasized;
            AnimateOpacity(HintGlass, emphasized || _inputPaused ? 0.96 : 0.42);
        }
        private static void AnimateOpacity(UIElement element, double target)
        {
            DoubleAnimation animation = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(170),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        private void ToggleToolbar(object sender, RoutedEventArgs e)
        {
            ReleaseRemoteInput();
            _toolbarCollapsed = !_toolbarCollapsed;
            ToolbarActions.Visibility = _toolbarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ToolbarDivider.Visibility = _toolbarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            StatusGlass.Visibility = _toolbarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ToolbarToggleIcon.RenderTransform = new RotateTransform(_toolbarCollapsed ? 180 : 0);
            ToolbarToggleButton.ToolTip = _toolbarCollapsed ? "顯示工具列" : "隱藏工具列";
            _chromeEmphasized = true;
            AnimateOpacity(ToolbarGlass, 1.0);
            if (!_toolbarCollapsed) AnimateOpacity(StatusGlass, 0.96);
            Viewer.Focus();
        }
        private void ToggleFullScreen(object sender, RoutedEventArgs e)
        {
            ReleaseRemoteInput();
            if (!_fullscreen) { _previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized; }
            else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = _previousState; }
            _fullscreen = !_fullscreen;
            FullScreenButton.ToolTip = _fullscreen ? "離開全螢幕" : "全螢幕";
            Viewer.Focus();
        }
        private static int Button(MouseButton button) { return button == MouseButton.Left ? 1 : button == MouseButton.Right ? 2 : button == MouseButton.Middle ? 3 : 0; }
        private static Dictionary<string, object> Pointer(string kind, Point point) { return new Dictionary<string, object> { {"kind", kind}, {"x", point.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, {"y", point.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} }; }
    }
    // Input HTTP requests must never block WPF's UI thread. Preserve key/button order,
    // replacing only consecutive moves so a slow link cannot replay a trail of old positions.
    internal sealed class RemoteInputQueue
    {
        private readonly object _sync = new object();
        private readonly LinkedList<Dictionary<string, object>> _queue = new LinkedList<Dictionary<string, object>>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Action<Dictionary<string, object>> _send;
        private readonly Func<bool> _connected;
        private bool _complete;
        public RemoteInputQueue(KvmPeerManager manager) : this(manager.SendInput, SamePeer(manager)) { }
        private static Func<bool> SamePeer(KvmPeerManager manager)
        {
            string peer = manager.ActivePeerIp;
            return delegate { return manager.Connected && string.Equals(peer, manager.ActivePeerIp, StringComparison.OrdinalIgnoreCase); };
        }
        internal RemoteInputQueue(Action<Dictionary<string, object>> send, Func<bool> connected) { _send = send; _connected = connected; new Thread(Run) { IsBackground = true, Name = "ChaoKvmUiInput" }.Start(); }
        public void Enqueue(Dictionary<string, object> item) { lock (_sync) { if (_complete) return; if ((string)item["kind"] == "move" && _queue.Last != null && (string)_queue.Last.Value["kind"] == "move") _queue.RemoveLast(); _queue.AddLast(item); _signal.Set(); } }
        public void Complete() { lock (_sync) { _complete = true; _signal.Set(); } }
        private void Run()
        {
            try { while (true) {
                Dictionary<string, object> item = null;
                lock (_sync) { if (_queue.Count > 0) { item = _queue.First.Value; _queue.RemoveFirst(); } else if (_complete) return; }
                if (item == null) { _signal.WaitOne(); continue; }
                if (!_connected()) continue;
                try { _send(item); } catch { }
            }} finally { _signal.Dispose(); }
        }
    }
    public static class ViewerGeometry
    {
        public static bool Normalize(Point point, double width, double height, int imageWidth, int imageHeight, bool clamp, out Point normalized)
        {
            normalized = new Point(); if (width <= 1 || height <= 1 || imageWidth <= 0 || imageHeight <= 0) return false;
            double scale = Math.Min(width / imageWidth, height / imageHeight);
            double rw = imageWidth * scale, rh = imageHeight * scale, ox = (width - rw) / 2, oy = (height - rh) / 2;
            if (!clamp && (point.X < ox || point.X > ox + rw || point.Y < oy || point.Y > oy + rh)) return false;
            normalized = new Point(Math.Max(0, Math.Min(1, (point.X - ox) / rw)), Math.Max(0, Math.Min(1, (point.Y - oy) / rh))); return true;
        }
    }
}
