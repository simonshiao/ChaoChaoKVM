using ChaoChaoKvm.Core;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ChaoChaoKvm
{
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private EventWaitHandle _activateEvent;
        private RegisteredWaitHandle _activateWait;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--ui-preview") >= 0)
            {
                base.OnStartup(e);
                return;
            }

            // A second LAN instance would try to bind HTTP 8001 again and
            // previously rendered a confusing ChaoTransfer port-conflict error.
            // Keep LAN and Online independent because they use different ports.
            string name = BuildFlavor.IsOnline
                ? @"Local\ChaoChaoKvm-Online-SingleInstance"
                : @"Local\ChaoChaoKvm-LAN-SingleInstance";
            bool firstInstance;
            _singleInstance = new Mutex(true, name, out firstInstance);
            if (!firstInstance)
            {
                // Ask the original process to bring its window forward, rather
                // than killing a possibly active remote-control session.
                bool signalled = false;
                for (int attempt = 0; attempt < 5 && !signalled; attempt++)
                {
                    try
                    {
                        using (EventWaitHandle existing = EventWaitHandle.OpenExisting(name + "-Activate"))
                        {
                            signalled = existing.Set();
                        }
                    }
                    catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
                }
                if (!signalled)
                    MessageBox.Show("吵吵 KVM 已在執行，但舊版尚不支援自動喚醒。請先切回原視窗，或正常結束舊版後重開。",
                        "吵吵 KVM 已開啟", MessageBoxButton.OK, MessageBoxImage.Information);
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown();
                return;
            }
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Activate");
            _activateWait = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                delegate(object state, bool timedOut)
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        ChaoChaoKvm.MainWindow window = MainWindow as ChaoChaoKvm.MainWindow;
                        if (window != null) window.RestoreExistingWindow();
                    }), DispatcherPriority.ApplicationIdle);
                }, null, Timeout.Infinite, false);
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_activateWait != null) { _activateWait.Unregister(null); _activateWait = null; }
            if (_activateEvent != null) { _activateEvent.Dispose(); _activateEvent = null; }
            if (_singleInstance != null)
            {
                try { _singleInstance.ReleaseMutex(); }
                catch (ApplicationException) { }
                _singleInstance.Dispose();
                _singleInstance = null;
            }
            base.OnExit(e);
        }
    }
}
