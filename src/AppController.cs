using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace FlashBar
{
    // 连接 shell 通知、列表、胶囊与托盘
    internal sealed class AppController : ApplicationContext
    {
        private readonly AttentionList _list = new AttentionList();
        private readonly ShellHookWindow _hook;
        private readonly PillForm _pill;
        private readonly Tray _tray;
        private readonly Timer _sweep;
        private readonly uint _myPid;
        private TestWindow _test;

        public AppController(bool demo)
        {
            _myPid = (uint)Process.GetCurrentProcess().Id;
            _pill = new PillForm(_list);
            _pill.ActivateRequested += delegate { OnPillLeft(); };
            _pill.DismissRequested += delegate { OnPillRight(); };

            _hook = new ShellHookWindow();
            _hook.Flash += OnFlash;
            _hook.Activated += OnActivated;
            _hook.Destroyed += OnDestroyed;

            _tray = new Tray();
            _tray.TestRequested += delegate { StartTest(); };
            _tray.ExitRequested += delegate { ExitThread(); };
            _tray.IconClicked += delegate { _pill.ExpandFor(8); };

            _sweep = new Timer();
            _sweep.Interval = 1000;
            _sweep.Tick += delegate { Guard("Sweep", delegate { if (_list.Sweep()) Changed(false); else _pill.CheckLayout(); }); };
            _sweep.Start();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Log.Info("started, pid=" + _myPid);
            if (demo)
            {
                // --demo：启动后自动演示一次（等同托盘「测试提醒」）
                Timer t = new Timer();
                t.Interval = 800;
                t.Tick += delegate { t.Stop(); t.Dispose(); Guard("Demo", StartTest); };
                t.Start();
            }
        }

        private static void Guard(string where, MethodInvoker a)
        {
            try { a(); }
            catch (Exception ex) { Log.Error(where, ex); }
        }

        private void Changed(bool expand)
        {
            _pill.UpdateView(expand);
            _tray.SetCount(_list.Count);
        }

        private bool IsTestWindow(IntPtr h)
        {
            return _test != null && !_test.IsDisposed && _test.IsHandleCreated && _test.Handle == h;
        }

        private void OnFlash(IntPtr h)
        {
            Guard("OnFlash", delegate
            {
                if (h == IntPtr.Zero || !NativeMethods.IsWindow(h)) return;
                if (WinUtil.ProcessId(h) == _myPid && !IsTestWindow(h)) return;
                // 不可见窗口（如 WSLg 的隐藏 RemoteApp 容器）在原生任务栏上也没有按钮，忽略
                if (!WinUtil.IsShown(h)) return;
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && WinUtil.RootOwner(fg) == WinUtil.RootOwner(h)) return;
                FlashResult r = _list.AddOrRefresh(h);
                if (r != FlashResult.Ignored) Changed(r == FlashResult.Expand);
            });
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            Guard("DisplayChanged", delegate { _pill.Reposition(); });
        }

        private void OnActivated(IntPtr h)
        {
            Guard("OnActivated", delegate { if (_list.RemoveRelated(h)) Changed(false); });
        }

        private void OnDestroyed(IntPtr h)
        {
            Guard("OnDestroyed", delegate
            {
                if (_list.RemoveDestroyed(h)) Changed(false);
            });
        }

        private void OnPillLeft()
        {
            AttentionItem it = _list.Latest;
            if (it == null) return;
            IntPtr h = it.Hwnd;
            bool ok = WinActivate.Activate(h);
            Log.Info("click-activate 0x" + h.ToString("X") + " ok=" + ok + " fg=0x" + NativeMethods.GetForegroundWindow().ToString("X"));
            // 切换失败（管理员窗口、前台锁等）时保留提醒，避免静默丢失；可右键忽略
            if (ok && _list.Dismiss(it)) Changed(false);
        }

        private void OnPillRight()
        {
            AttentionItem it = _list.Latest;
            if (it == null) return;
            Log.Info("dismiss 0x" + it.Hwnd.ToString("X"));
            if (_list.Dismiss(it)) Changed(false);
        }

        private void StartTest()
        {
            if (_test != null && !_test.IsDisposed) _test.Close();
            _test = new TestWindow();
            _test.FormClosed += delegate { _test = null; };
            _test.Launch();
        }

        protected override void ExitThreadCore()
        {
            Guard("Exit", delegate
            {
                _sweep.Stop();
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                _hook.Dispose();
                _tray.Dispose();
                if (_test != null && !_test.IsDisposed) _test.Close();
                _pill.Close();
                Log.Info("exit");
            });
            base.ExitThreadCore();
        }
    }
}
