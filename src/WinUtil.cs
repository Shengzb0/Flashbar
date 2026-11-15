using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using N = FlashBar.NativeMethods;

namespace FlashBar
{
    // 窗口/进程信息辅助
    internal static class WinUtil
    {
        private static readonly Dictionary<string, string> NameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static IntPtr RootOwner(IntPtr h)
        {
            IntPtr r = N.GetAncestor(h, N.GA_ROOTOWNER);
            return r == IntPtr.Zero ? h : r;
        }

        // 窗口或其 root owner 可见（最小化也算可见）
        public static bool IsShown(IntPtr h)
        {
            if (N.IsWindowVisible(h)) return true;
            IntPtr r = RootOwner(h);
            return r != h && N.IsWindowVisible(r);
        }

        public static uint ProcessId(IntPtr h)
        {
            uint pid;
            N.GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        public static string Title(IntPtr h)
        {
            int len = N.GetWindowTextLength(h);
            if (len <= 0) return "";
            StringBuilder sb = new StringBuilder(len + 1);
            N.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ExePath(uint pid)
        {
            IntPtr hp = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hp == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return N.QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { N.CloseHandle(hp); }
        }

        // 友好名：文件描述 > exe 名
        public static string AppName(uint pid)
        {
            string exe = null;
            try { exe = ExePath(pid); }
            catch (Exception ex) { Log.Error("AppName.ExePath", ex); }
            if (string.IsNullOrEmpty(exe)) return "未知程序";
            return ExeName(exe);
        }

        // 按 exe 路径缓存：PID 会被系统复用，按 PID 缓存会把新进程显示成旧程序名
        // （通知监听线程也会调用，所以加锁）
        public static string ExeName(string exe)
        {
            lock (NameCache)
            {
                string name;
                if (NameCache.TryGetValue(exe, out name)) return name;
                name = Path.GetFileNameWithoutExtension(exe);
                try
                {
                    string desc = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                    if (!string.IsNullOrEmpty(desc) && desc.Trim().Length > 0) name = desc.Trim();
                }
                catch (Exception ex) { Log.Error("AppName", ex); }
                if (NameCache.Count > 200) NameCache.Clear();
                NameCache[exe] = name;
                return name;
            }
        }

        // 取窗口图标，失败时用 exe 图标
        public static Bitmap GetIcon(IntPtr h, uint pid, int size)
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                int[] kinds = { N.ICON_SMALL2, N.ICON_SMALL, N.ICON_BIG };
                foreach (int k in kinds)
                {
                    IntPtr res;
                    if (N.SendMessageTimeout(h, N.WM_GETICON, (IntPtr)k, IntPtr.Zero, N.SMTO_ABORTIFHUNG, 100, out res) != IntPtr.Zero
                        && res != IntPtr.Zero) { hIcon = res; break; }
                }
                if (hIcon == IntPtr.Zero) hIcon = N.GetClassLongPtr(h, N.GCLP_HICONSM);
                if (hIcon == IntPtr.Zero) hIcon = N.GetClassLongPtr(h, N.GCLP_HICON);
                if (hIcon != IntPtr.Zero)
                {
                    using (Icon ic = Icon.FromHandle(hIcon))
                        return Scale(ic.ToBitmap(), size);
                }
            }
            catch (Exception ex) { Log.Error("GetIcon", ex); }
            try
            {
                string exe = ExePath(pid);
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    using (Icon ic = Icon.ExtractAssociatedIcon(exe))
                        if (ic != null) return Scale(ic.ToBitmap(), size);
                }
            }
            catch (Exception ex) { Log.Error("GetIcon.exe", ex); }
            return null;
        }

        // 缩放到 size×size；尺寸不同时会释放 src
        internal static Bitmap Scale(Bitmap src, int size)
        {
            if (src.Width == size && src.Height == size) return src;
            Bitmap dst = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            src.Dispose();
            return dst;
        }
    }
}
