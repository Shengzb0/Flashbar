using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FlashBar
{
    // 托盘图标与菜单
    internal sealed class Tray : IDisposable
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "FlashBar";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _autoStart;
        private Icon _img;

        public event EventHandler TestRequested;
        public event EventHandler ExitRequested;
        public event EventHandler IconClicked;

        public Tray()
        {
            _img = MakeIcon(SystemInformation.SmallIconSize.Width);
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("测试提醒", null, delegate { Raise(TestRequested); });
            _autoStart = new ToolStripMenuItem("开机自启");
            _autoStart.Click += delegate { ToggleAutoStart(); };
            menu.Items.Add(_autoStart);
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

        public void SetCount(int n)
        {
            try { _icon.Text = n > 0 ? "FlashBar：" + n + " 个窗口请求关注" : "FlashBar 闪烁提醒"; }
            catch (Exception ex) { Log.Error("Tray.SetCount", ex); }
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
