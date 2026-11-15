using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using N = FlashBar.NativeMethods;

namespace FlashBar
{
    // 托盘「测试提醒」用的演示窗口：最小化后闪烁，被打开 5 秒后自动关闭
    internal sealed class TestWindow : Form
    {
        private readonly Timer _life;
        private bool _flashed;

        public TestWindow()
        {
            Text = "FlashBar 测试窗口";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(320, 90);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            ShowInTaskbar = true;
            Label lb = new Label();
            lb.Dock = DockStyle.Fill;
            lb.TextAlign = ContentAlignment.MiddleCenter;
            lb.Text = "这是 FlashBar 的测试窗口。\n它会在 5 秒后自动关闭。";
            Controls.Add(lb);

            _life = new Timer();
            _life.Interval = 60000; // 没人理也在 60 秒后关闭
            _life.Tick += delegate { Close(); };
        }

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        private const int SW_SHOWMINNOACTIVE = 7;

        // 以不激活的最小化状态显示，稍后开始闪烁
        public void Launch()
        {
            WindowState = FormWindowState.Normal;
            IntPtr h = Handle;
            CenterToScreen();
            ShowWindow(h, SW_SHOWMINNOACTIVE);
            _life.Start();
            Timer t = new Timer();
            t.Interval = 400;
            t.Tick += delegate { t.Stop(); t.Dispose(); if (!IsDisposed) StartFlash(); };
            t.Start();
        }

        private void StartFlash()
        {
            N.FLASHWINFO fi = new N.FLASHWINFO();
            fi.cbSize = (uint)Marshal.SizeOf(typeof(N.FLASHWINFO));
            fi.hwnd = Handle;
            fi.dwFlags = N.FLASHW_TRAY | N.FLASHW_TIMERNOFG;
            fi.uCount = 0;
            fi.dwTimeout = 0;
            N.FlashWindowEx(ref fi);
            _flashed = true;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_flashed && WindowState != FormWindowState.Minimized)
            {
                _life.Stop();
                _life.Interval = 5000;
                _life.Start();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _life.Dispose();
            base.OnFormClosed(e);
        }
    }
}
