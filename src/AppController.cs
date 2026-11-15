using System;
using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FlashBar
{
    // 连接 shell 通知、系统通知（实验）、列表、胶囊与托盘
    internal sealed class AppController : ApplicationContext
    {
        private const string ToastKey = "ToastTakeover";

        private readonly AttentionList _list = new AttentionList();
        private readonly ShellHookWindow _hook;
        private readonly PillForm _pill;
        private readonly Tray _tray;
        private readonly Timer _sweep;
        private readonly uint _myPid;
        private TestWindow _test;
        private ToastWatcher _toasts;   // 实验功能「接管系统通知」，未开启时为 null
        private string _toastNote;      // 设置开着但没在运行的原因（显示在菜单上）
        private bool _toastAsking;      // 正在等系统授权结果

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
            _tray.ToastToggleRequested += delegate { Guard("ToastToggle", ToggleToasts); };
            _tray.ToastTestRequested += delegate { Guard("ToastTest", SendTestToast); };
            _tray.ToastAppToggled += delegate(string aumid, bool excluded) { Guard("ToastApp", delegate { OnToastAppToggled(aumid, excluded); }); };

            _sweep = new Timer();
            _sweep.Interval = 1000;
            _sweep.Tick += delegate { Guard("Sweep", delegate { if (_list.Sweep()) Changed(false); else _pill.CheckLayout(); }); };
            _sweep.Start();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
            Log.Info("started, pid=" + _myPid);

            // 上次没正常退出时横幅可能还关着：按设置对账——开着就接着接管，关着就全部还原
            if (Settings.GetBool(ToastKey, false)) Guard("ToastStart", delegate { StartToasts(false); });
            else Guard("ToastRestore", ToastBanner.Deactivate);
            UpdateToastMenu();

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
            _tray.SetCount(_list.FlashCount, _list.ToastCount);
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

        // 注销 / 关机：先把横幅还给系统，免得下次登录、FlashBar 还没起来时通知不弹
        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Guard("SessionEnding", ToastBanner.RestoreAll);
        }

        private void OnActivated(IntPtr h)
        {
            Guard("OnActivated", delegate
            {
                bool a = _list.RemoveRelated(h);
                bool b = _list.ToastAppActivated(h);
                if (a || b) Changed(false);
            });
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
            if (it.Toast != null)
            {
                if (it.Toast.IsTest)
                {
                    // 测试通知没有真正的来源程序可切，点掉即可
                    ToastWatcher.ClearTest();
                    if (_list.Dismiss(it)) Changed(false);
                    return;
                }
                // 通知条目：切到 / 启动来源程序；原通知留在通知中心不动
                bool done = ToastApps.Activate(it.Toast.Source);
                Log.Info("click-toast " + (it.Toast.Source.Aumid ?? "?") + " ok=" + done);
                if (done && _list.Dismiss(it)) Changed(false);
                return;
            }
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
            Log.Info(it.Toast != null ? "dismiss toast id=" + it.Toast.Id : "dismiss 0x" + it.Hwnd.ToString("X"));
            if (it.Toast != null && it.Toast.IsTest) ToastWatcher.ClearTest();
            if (_list.Dismiss(it)) Changed(false);
        }

        private void StartTest()
        {
            if (_test != null && !_test.IsDisposed) _test.Close();
            _test = new TestWindow();
            _test.FormClosed += delegate { _test = null; };
            _test.Launch();
        }

        // ---- 实验功能：接管系统通知 ----

        private void ToggleToasts()
        {
            if (_toastAsking) return;
            if (Settings.GetBool(ToastKey, false))
            {
                Settings.SetBool(ToastKey, false);
                StopToasts();
                _toastNote = null;
                UpdateToastMenu();
                Log.Info("toast takeover off");
                return;
            }
            StartToasts(true);
        }

        // interactive=true：用户刚在菜单里勾选，可以弹授权询问和提示框；false：启动时静默对账
        private void StartToasts(bool interactive)
        {
            string err;
            int st = ToastWatcher.QueryAccess(out err);
            if (st == ToastWatcher.Allowed)
            {
                if (interactive) Settings.SetBool(ToastKey, true);
                BeginToasts();
                return;
            }
            if (st == ToastWatcher.Unsupported)
            {
                ToastBanner.Deactivate();
                _toastNote = interactive ? null : "不可用";
                UpdateToastMenu();
                if (interactive)
                    MessageBox.Show("这台电脑上无法读取通知中心（需要 Windows 10 1607 或更新版本）。\n\n" + err,
                        "FlashBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!interactive)
            {
                // 启动时没有权限：不弹窗，横幅交还系统，菜单上标注；设置保留，下次启动再试
                Log.Info("toast takeover: access status " + st + ", not started");
                ToastBanner.Deactivate();
                _toastNote = "未授权";
                UpdateToastMenu();
                return;
            }
            if (st == ToastWatcher.Denied) { AskForAccess(); return; }
            _toastAsking = true;
            _toastNote = "等待授权";
            UpdateToastMenu();
            ToastWatcher.RequestAccess(delegate(int r)
            {
                _toastAsking = false;
                _toastNote = null;
                Guard("ToastAccess", delegate
                {
                    Log.Info("toast access requested, result=" + r);
                    if (r == ToastWatcher.Allowed)
                    {
                        Settings.SetBool(ToastKey, true);
                        BeginToasts();
                    }
                    else AskForAccess();
                });
                UpdateToastMenu();
            });
        }

        private void AskForAccess()
        {
            UpdateToastMenu();
            DialogResult r = MessageBox.Show(
                "FlashBar 还没有读取通知的权限。\n\n" +
                "请在「设置 → 隐私和安全性 → 通知」里打开通知访问权限，然后回到托盘重新勾选「接管系统通知」。\n\n" +
                "现在打开这个设置页吗？",
                "FlashBar", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) Tray.OpenUri("ms-settings:privacy-notifications");
        }

        private void BeginToasts()
        {
            if (_toasts != null) { _toasts.Dispose(); _toasts = null; }
            // 先不关横幅：监听线程拿到基线后再关，免得中间这一下来的通知两头都看不到
            _toasts = new ToastWatcher(ToastBanner.Begin());
            _toasts.Arrived += OnToastArrived;
            _toasts.Gone += OnToastGone;
            _toasts.Failed += OnToastFailed;
            _toasts.Start();
            _toastNote = null;
            UpdateToastMenu();
            Log.Info("toast takeover on");
        }

        private void StopToasts()
        {
            if (_toasts != null) { _toasts.Dispose(); _toasts = null; }
            ToastBanner.Deactivate();
            if (_list.RemoveToasts(null)) Changed(false);
        }

        private void OnToastArrived(ToastWatcher sender, ToastInfo t)
        {
            if (sender != _toasts)
            {
                if (t.Icon != null) { t.Icon.Dispose(); t.Icon = null; }
                return;
            }
            Guard("ToastArrived", delegate
            {
                _list.AddToast(t);
                Changed(true);
            });
        }

        private void OnToastGone(ToastWatcher sender, uint id)
        {
            if (sender != _toasts) return;
            Guard("ToastGone", delegate { if (_list.RemoveToast(id)) Changed(false); });
        }

        // 监听停了（权限被收回、系统组件出错）：横幅马上还给系统，设置保留，下次启动再试
        private void OnToastFailed(ToastWatcher sender, string reason)
        {
            if (sender != _toasts) return;
            Guard("ToastFailed", delegate
            {
                Log.Info("toast takeover stopped: " + reason);
                StopToasts();
                _toastNote = reason;
                UpdateToastMenu();
            });
        }

        private void OnToastAppToggled(string aumid, bool excluded)
        {
            ToastBanner.SetExcluded(aumid, excluded);
            if (excluded && _list.RemoveToasts(aumid)) Changed(false);
        }

        private void SendTestToast()
        {
            try
            {
                // 接管中：先把测试用的身份（Windows PowerShell）纳入接管，第一条测试通知就不会再弹系统横幅
                if (_toasts != null) ToastBanner.Adopt(ToastWatcher.TestAumid, "Windows PowerShell");
                ToastWatcher.ShowTest();
                Log.Info("test toast sent");
            }
            catch (Exception ex)
            {
                ex = WinRT.Inner(ex);
                Log.Error("TestToast", ex);
                MessageBox.Show("发送测试通知失败：" + ex.Message, "FlashBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateToastMenu()
        {
            _tray.SetToastState(Settings.GetBool(ToastKey, false) || _toastAsking, _toastNote);
        }

        protected override void ExitThreadCore()
        {
            Guard("Exit", delegate
            {
                _sweep.Stop();
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.SessionEnding -= OnSessionEnding;
                // 退出时把横幅还给系统：FlashBar 不在时通知照常弹
                Guard("Exit.Toasts", delegate
                {
                    if (_toasts != null) { _toasts.Dispose(); _toasts = null; }
                    ToastBanner.Deactivate();
                });
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
