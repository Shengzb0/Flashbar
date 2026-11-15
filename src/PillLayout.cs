using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using N = FlashBar.NativeMethods;

namespace FlashBar
{
    // 胶囊定位：跟随光标所在显示器、避开 Seelen Dock、全屏时隐藏、细线态点击穿透
    internal sealed partial class PillForm
    {
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out N.RECT r);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, DWMWA_CLOAKED = 14;
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
        private const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E;

        private string _layoutKey;
        private bool _suppressed;     // 全屏期间隐藏（列表保留）

        private static Rectangle ToRect(N.RECT r) { return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }

        private static IntPtr TargetMonitor()
        {
            return N.MonitorFromPoint(Cursor.Position, N.MONITOR_DEFAULTTONEAREST);
        }

        // 计算胶囊中心 x、底边 y；返回布局指纹，用于判断是否需要重新定位
        private string ComputeAnchor(out Rectangle mon, out float s, out int cx, out int bottom)
        {
            Rectangle work = WinActivate.Monitor(TargetMonitor(), out mon, out s);
            cx = work.Left + work.Width / 2;
            bottom = work.Bottom - (int)Math.Round(8 * s);
            int dock = DockTop(mon, cx);
            if (dock >= 0) bottom = Math.Min(bottom, dock - (int)Math.Round(6 * s));
            return mon + "|" + work + "|" + s + "|" + bottom;
        }

        // Seelen UI 的 Dock 不占工作区：找贴近底边、跨过屏幕中线的 seelen 可见窗口，取其顶边
        private static int DockTop(Rectangle mon, int cx)
        {
            int top = int.MaxValue;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!N.IsWindowVisible(h) || N.IsIconic(h)) return true;
                    N.RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    Rectangle rc = ToRect(r);
                    if (rc.Width <= 0 || rc.Height <= 0 || rc.Height > mon.Height / 3) return true;
                    if (rc.Left > cx || rc.Right < cx) return true;
                    if (rc.Top >= mon.Bottom || rc.Bottom < mon.Bottom - mon.Height / 8 || rc.Top < mon.Top) return true;
                    int cloaked;
                    if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                    string exe = WinUtil.ExePath(WinUtil.ProcessId(h));
                    if (string.IsNullOrEmpty(exe) || !Path.GetFileName(exe).StartsWith("seelen", StringComparison.OrdinalIgnoreCase)) return true;
                    if (rc.Top < top) top = rc.Top;
                }
                catch (Exception ex) { Log.Error("DockTop", ex); }
                return true;
            }, IntPtr.Zero);
            return top == int.MaxValue ? -1 : top;
        }

        // 全屏视频/游戏/演示：前台窗口正好铺满胶囊所在显示器，或系统报告独占全屏/演示模式
        private bool FullscreenOn(Rectangle mon)
        {
            try
            {
                int st;
                if (SHQueryUserNotificationState(out st) == 0 && (st == QUNS_RUNNING_D3D_FULL_SCREEN || st == QUNS_PRESENTATION_MODE)) return true;
            }
            catch (Exception ex) { Log.Error("QUNS", ex); }
            IntPtr fg = N.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle || fg == GetShellWindow()) return false;
            StringBuilder sb = new StringBuilder(64);
            GetClassName(fg, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;
            N.RECT r;
            return GetWindowRect(fg, out r) && ToRect(r) == mon;
        }

        // 细线态点击穿透（悬停展开靠光标轮询，不依赖命中测试）；展开态可点击
        private void SetClickThrough(bool on)
        {
            long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            long nx = on ? (ex | WS_EX_TRANSPARENT) : (ex & ~(long)WS_EX_TRANSPARENT);
            if (nx != ex) SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(nx));
        }

        // 每秒巡检：显示器/缩放/工作区/Dock 位置变化时重新定位
        public void CheckLayout()
        {
            if (_list.Count == 0 || (!Visible && !_suppressed)) return;
            Rectangle mon; float s; int cx, bottom;
            if (ComputeAnchor(out mon, out s, out cx, out bottom) != _layoutKey) Render();
        }

        // 悬停计时器里调用：进入/退出全屏时隐藏/恢复；返回 true 表示当前被抑制
        private bool CheckFullscreen()
        {
            Rectangle mon;
            float s;
            WinActivate.Monitor(TargetMonitor(), out mon, out s);
            bool fs = FullscreenOn(mon);
            if (fs == _suppressed) return fs;
            _suppressed = fs;
            Log.Info(fs ? "fullscreen: hide pill" : "fullscreen ended: show pill");
            if (fs) { if (Visible) Hide(); }
            else Render();
            return fs;
        }
    }
}
