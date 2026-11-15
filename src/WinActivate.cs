using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using N = FlashBar.NativeMethods;

namespace FlashBar
{
    // 显示器信息与窗口激活
    internal static class WinActivate
    {
        [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hWnd);

        // 返回显示器工作区（物理像素），同时给出整屏矩形和缩放比例
        public static Rectangle Monitor(IntPtr mon, out Rectangle monRect, out float scale)
        {
            scale = 1f;
            try
            {
                uint dx, dy;
                if (N.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) scale = dx / 96f;
            }
            catch (Exception ex) { Log.Error("GetDpiForMonitor", ex); }
            N.MONITORINFO mi = new N.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(N.MONITORINFO));
            if (!N.GetMonitorInfo(mon, ref mi))
            {
                monRect = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                return System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            }
            N.RECT m = mi.rcMonitor, r = mi.rcWork;
            monRect = Rectangle.FromLTRB(m.Left, m.Top, m.Right, m.Bottom);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        // 尽量可靠地把窗口切到前台
        public static bool Activate(IntPtr h)
        {
            if (!N.IsWindow(h)) return false;
            if (!N.IsWindowVisible(h))
            {
                // 被程序隐藏的从属窗口（owner 最小化时对话框会被系统隐藏）：不强行 SW_SHOW，
                // 改为还原 root owner，再切到它最近活动的弹出窗口
                IntPtr root = WinUtil.RootOwner(h);
                if (root == h) return false;
                if (N.IsIconic(root)) N.ShowWindow(root, N.SW_RESTORE);
                IntPtr pop = GetLastActivePopup(root);
                h = (pop != IntPtr.Zero && N.IsWindowVisible(pop)) ? pop : root;
                if (!N.IsWindowVisible(h)) return false;
            }
            else if (N.IsIconic(h)) N.ShowWindow(h, N.SW_RESTORE);

            if (TrySet(h)) return true;

            uint dummy;
            IntPtr fg = N.GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? N.GetWindowThreadProcessId(fg, out dummy) : 0;
            uint me = N.GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != me) attached = N.AttachThreadInput(me, fgThread, true);
                N.BringWindowToTop(h);
                N.SetForegroundWindow(h);
            }
            finally
            {
                if (attached) N.AttachThreadInput(me, fgThread, false);
            }
            if (IsFore(h)) return true;

            N.SwitchToThisWindow(h, true);
            Thread.Sleep(50);
            return IsFore(h);
        }

        private static bool TrySet(IntPtr h)
        {
            N.SetForegroundWindow(h);
            return IsFore(h);
        }

        private static bool IsFore(IntPtr h)
        {
            IntPtr fg = N.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            return fg == h || WinUtil.RootOwner(fg) == WinUtil.RootOwner(h);
        }
    }
}
