using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FlashBar
{
    internal delegate void ToastAppHandler(string aumid, bool excluded);

    // 托盘图标与菜单
    internal sealed class Tray : IDisposable
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "FlashBar";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ToastText = "接管系统通知";

        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _autoStart;
        private readonly ToolStripMenuItem _toast;
        private readonly ToolStripMenuItem _toastApps;
        private Icon _img;

        public event EventHandler TestRequested;
        public event EventHandler ExitRequested;
        public event EventHandler IconClicked;
        public event EventHandler ToastToggleRequested;
        public event EventHandler ToastTestRequested;
        public event ToastAppHandler ToastAppToggled;

        public Tray()
        {
            _img = MakeIcon(SystemInformation.SmallIconSize.Width);
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("测试提醒", null, delegate { Raise(TestRequested); });
            _autoStart = new ToolStripMenuItem("开机自启");
            _autoStart.Click += delegate { ToggleAutoStart(); };
            menu.Items.Add(_autoStart);

            // 实验功能：接管系统通知
            ToolStripMenuItem exp = new ToolStripMenuItem("实验功能");
            exp.DropDown.ShowItemToolTips = true;
            _toast = new ToolStripMenuItem(ToastText);
            _toast.ToolTipText = "把通知中心的新通知显示在底部胶囊里，并关掉这些应用的系统横幅（通知仍会进通知中心）";
            _toast.Click += delegate { Raise(ToastToggleRequested); };
            _toastApps = new ToolStripMenuItem("已接管的应用");
            _toastApps.DropDown.ShowItemToolTips = true;
            _toastApps.DropDownItems.Add(new ToolStripMenuItem("…"));   // 占位，打开时再填
            _toastApps.DropDownOpening += delegate { FillToastApps(); };
            exp.DropDownItems.Add(_toast);
            exp.DropDownItems.Add(_toastApps);
            exp.DropDownItems.Add("发送测试通知", null, delegate { Raise(ToastTestRequested); });
            exp.DropDownItems.Add(new ToolStripSeparator());
            exp.DropDownItems.Add("系统通知设置…", null, delegate { OpenUri("ms-settings:notifications"); });
            exp.DropDownItems.Add("通知访问权限…", null, delegate { OpenUri("ms-settings:privacy-notifications"); });
            menu.Items.Add(exp);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Raise(ExitRequested); });
            menu.Opening += delegate { _autoStart.Checked = IsAutoStart(); };

            _icon = new NotifyIcon();
            _icon.Icon = _img;
            _icon.Text = "FlashBar 闪烁提醒";
            _icon.ContextMenuStrip = menu;
            _icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Raise(IconClicked);
            };
            _icon.Visible = true;
        }

        public void SetCount(int flashes, int toasts)
        {
            try
            {
                string t;
                if (flashes > 0 && toasts > 0) t = "FlashBar：" + flashes + " 个窗口请求关注，" + toasts + " 条通知";
                else if (flashes > 0) t = "FlashBar：" + flashes + " 个窗口请求关注";
                else if (toasts > 0) t = "FlashBar：" + toasts + " 条通知";
                else t = "FlashBar 闪烁提醒";
                _icon.Text = t;
            }
            catch (Exception ex) { Log.Error("Tray.SetCount", ex); }
        }

        // on = 设置里开着；note = 开着但没在运行的原因（未授权 / 不可用 / 出错 / 等待授权）
        public void SetToastState(bool on, string note)
        {
            _toast.Checked = on;
            _toast.Text = note == null ? ToastText : ToastText + "（" + note + "）";
        }

        public static void OpenUri(string uri)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(uri);
                psi.UseShellExecute = true;
                using (Process p = Process.Start(psi)) { }
            }
            catch (Exception ex) { Log.Error("OpenUri " + uri, ex); }
        }

        // 「已接管的应用」：勾选 = 由 FlashBar 显示（系统横幅关闭），取消 = 交还系统
        private void FillToastApps()
        {
            try
            {
                ToolStripItemCollection items = _toastApps.DropDownItems;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    ToolStripItem old = items[i];
                    items.RemoveAt(i);
                    old.Dispose();
                }
                ToolStripMenuItem head = new ToolStripMenuItem("勾选＝FlashBar 显示，取消＝交还系统横幅");
                head.Enabled = false;
                items.Add(head);
                items.Add(new ToolStripSeparator());
                List<ToastApp> apps = ToastBanner.Snapshot();
                if (apps.Count == 0)
                {
                    ToolStripMenuItem none = new ToolStripMenuItem("（还没有收到过应用通知）");
                    none.Enabled = false;
                    items.Add(none);
                    return;
                }
                foreach (ToastApp a in apps)
                {
                    string aumid = a.Aumid;
                    bool excluded = a.Excluded;
                    ToolStripMenuItem mi = new ToolStripMenuItem(string.IsNullOrEmpty(a.Name) ? aumid : a.Name);
                    mi.Checked = !excluded;
                    mi.ToolTipText = aumid;
                    mi.Click += delegate
                    {
                        ToastAppHandler h = ToastAppToggled;
                        if (h != null) h(aumid, !excluded);
                    };
                    items.Add(mi);
                }
            }
            catch (Exception ex) { Log.Error("Tray.FillToastApps", ex); }
        }

        private void Raise(EventHandler h)
        {
            try { if (h != null) h(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("Tray.Raise", ex); }
        }

        // 运行时绘制橙色圆点图标
        private static Icon MakeIcon(int size)
        {
            if (size < 16) size = 16;
            using (Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float pad = size * 0.12f;
                    RectangleF r = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(0xF7, 0xA2, 0x1B)))
                        g.FillEllipse(b, r);
                    using (Pen p = new Pen(Color.FromArgb(160, 150, 80, 0), Math.Max(1f, size / 16f)))
                        g.DrawEllipse(p, r);
                }
                IntPtr h = bmp.GetHicon();
                using (Icon tmp = Icon.FromHandle(h))
                {
                    Icon copy = (Icon)tmp.Clone();
                    NativeMethods.DestroyIcon(h);
                    return copy;
                }
            }
        }

        private static string ExeCommand()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        public static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    object v = k == null ? null : k.GetValue(RunName);
                    if (v == null || !string.Equals(v.ToString(), ExeCommand(), StringComparison.OrdinalIgnoreCase)) return false;
                }
                return !DisabledInTaskManager();
            }
            catch (Exception ex) { Log.Error("IsAutoStart", ex); return false; }
        }

        // 任务管理器「启动」页禁用时写在 StartupApproved\Run，首字节为奇数表示已禁用
        private static bool DisabledInTaskManager()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
            {
                byte[] b = k == null ? null : k.GetValue(RunName) as byte[];
                return b != null && b.Length > 0 && (b[0] & 1) != 0;
            }
        }

        private static void ClearApproved()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                if (k != null) k.DeleteValue(RunName, false);
        }

        private void ToggleAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (IsAutoStart()) k.DeleteValue(RunName, false);
                    else k.SetValue(RunName, ExeCommand());
                }
                ClearApproved();   // 开启时清掉任务管理器的禁用标记；关闭时顺带清理残留
                _autoStart.Checked = IsAutoStart();
                Log.Info("autostart=" + _autoStart.Checked);
            }
            catch (Exception ex)
            {
                Log.Error("ToggleAutoStart", ex);
                MessageBox.Show("无法修改开机自启设置：" + ex.Message, "FlashBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            if (_img != null) { _img.Dispose(); _img = null; }
        }
    }
}
