using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using N = FlashBar.NativeMethods;

namespace FlashBar
{
    // 通知来源应用（按 AUMID 解析一次后缓存）。创建后只读、可跨线程读取；Icon 只在监听线程用，交给界面前先复制
    internal sealed class ToastSource
    {
        public string Aumid;              // AppUserModelID，取不到时为 null
        public string Name;
        public string Pfn;                // 打包应用的 PackageFamilyName
        public string ExePath;            // 开始菜单快捷方式的目标 / 从 AUMID 推出的 exe
        public string ExeDir, ExeName;    // Squirrel 启动器（Update.exe --processStart X.exe）：按目录 + 文件名匹配
        public bool InAppsFolder;         // 「所有应用」(shell:AppsFolder) 里有它，可以用来启动
        public Bitmap Icon;
    }

    // 通知来源应用：AUMID -> 名称 / 图标 / exe；匹配窗口；左键时切过去或启动
    internal static class ToastApps
    {
        private static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;

        // ---- 解析（监听线程，STA） ----

        public static void Describe(ToastSource s)
        {
            if (s.Aumid == null || !SafeAumid(s.Aumid)) return;
            IShellItem2 item = null;
            try
            {
                Guid iid = typeof(IShellItem2).GUID;
                if (SHCreateItemFromParsingName(AppsFolder(s.Aumid), IntPtr.Zero, ref iid, out item) == 0 && item != null)
                {
                    // 能解析但 ID 对不上，说明落到了别的项上
                    string id = GetString(item, PKEY_AppUserModel_ID);
                    s.InAppsFolder = id == null || string.Equals(id, s.Aumid, StringComparison.OrdinalIgnoreCase);
                    if (s.InAppsFolder)
                    {
                        SetTarget(s, GetString(item, PKEY_Link_TargetParsingPath), GetString(item, PKEY_Link_Arguments));
                        IntPtr pidl;
                        if (SHGetIDListFromObject(item, out pidl) == 0 && pidl != IntPtr.Zero)
                        {
                            try { FromPidl(pidl, s); }
                            finally { Marshal.FreeCoTaskMem(pidl); }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("ToastApps.AppsFolder " + s.Aumid, ex); }
            finally { if (item != null) Marshal.ReleaseComObject(item); }

            if (s.ExePath == null && s.ExeName == null) s.ExePath = ExeFromAumid(s.Aumid);
            try
            {
                if (s.Icon == null && s.ExePath != null)
                    using (Icon ic = Icon.ExtractAssociatedIcon(s.ExePath))
                        if (ic != null) s.Icon = WinUtil.Scale(ic.ToBitmap(), 32);
                if (s.Icon == null) s.Icon = RegistryIcon(s.Aumid);
            }
            catch (Exception ex) { Log.Error("ToastApps.Icon " + s.Aumid, ex); }
            if (string.IsNullOrEmpty(s.Name)) s.Name = RegistryName(s.Aumid);
            if (string.IsNullOrEmpty(s.Name) && s.ExePath != null) s.Name = WinUtil.ExeName(s.ExePath);
        }

        // 最后的兜底名字：去掉包名里的发布者哈希、路径和 .exe
        public static string ShortName(string aumid)
        {
            string s = aumid;
            int bang = s.IndexOf('!');
            if (bang > 0) s = s.Substring(0, bang);
            int slash = s.LastIndexOf('\\');
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            int us = s.LastIndexOf('_');
            if (us > 0 && s.Length - us == 14) s = s.Substring(0, us);
            return s.Length == 0 ? aumid : s;
        }

        private static void SetTarget(ToastSource s, string target, string args)
        {
            if (!IsExe(target)) return;
            if (string.Equals(Path.GetFileName(target), "Update.exe", StringComparison.OrdinalIgnoreCase))
            {
                string exe = ArgAfter(args, "--processStart");
                if (IsExe(exe))
                {
                    s.ExeDir = Path.GetDirectoryName(target).TrimEnd('\\') + "\\";
                    s.ExeName = Path.GetFileName(exe);
                    return;
                }
            }
            s.ExePath = target;
        }

        private static void FromPidl(IntPtr pidl, ToastSource s)
        {
            SHFILEINFO fi = new SHFILEINFO();
            uint flags = SHGFI_PIDL | SHGFI_ICON | SHGFI_LARGEICON | SHGFI_DISPLAYNAME;
            if (SHGetFileInfo(pidl, 0, ref fi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags) == IntPtr.Zero) return;
            if (string.IsNullOrEmpty(s.Name) && !string.IsNullOrEmpty(fi.szDisplayName)) s.Name = fi.szDisplayName;
            if (fi.hIcon == IntPtr.Zero) return;
            try
            {
                using (Icon ic = Icon.FromHandle(fi.hIcon))
                    s.Icon = WinUtil.Scale(ic.ToBitmap(), 32);
            }
            finally { N.DestroyIcon(fi.hIcon); }
        }

        // AUMID 自己就能推出 exe 的几种情况：{KNOWNFOLDERID}\相对路径.exe、绝对路径、注册了 COM 激活器（LocalServer32）
        private static string ExeFromAumid(string aumid)
        {
            try
            {
                int close = aumid.IndexOf('}');
                if (aumid.StartsWith("{") && close > 0 && close + 2 < aumid.Length && aumid[close + 1] == '\\')
                {
                    Guid g;
                    if (Guid.TryParse(aumid.Substring(0, close + 1), out g))
                    {
                        string root = KnownFolder(g);
                        if (root != null)
                        {
                            string p = Path.Combine(root, aumid.Substring(close + 2));
                            if (IsExe(p) && File.Exists(p)) return p;
                        }
                    }
                }
                if (IsExe(aumid) && Path.IsPathRooted(aumid) && File.Exists(aumid)) return aumid;
                string clsid = RegValue(@"Software\Classes\AppUserModelId\" + aumid, "CustomActivator");
                if (!string.IsNullOrEmpty(clsid))
                {
                    string exe = FirstExe(RegValue(@"Software\Classes\CLSID\" + clsid + @"\LocalServer32", ""));
                    if (IsExe(exe) && File.Exists(exe)) return exe;
                }
            }
            catch (Exception ex) { Log.Error("ToastApps.ExeFromAumid " + aumid, ex); }
            return null;
        }

        // 只注册在 HKCU\Software\Classes\AppUserModelId 里的应用（不在开始菜单）：用它登记的图标和名字
        private static Bitmap RegistryIcon(string aumid)
        {
            string uri = RegValue(@"Software\Classes\AppUserModelId\" + aumid, "IconUri");
            if (string.IsNullOrEmpty(uri)) return null;
            string path = Environment.ExpandEnvironmentVariables(uri.Trim().Trim('"'));
            if (!Path.IsPathRooted(path) || !File.Exists(path)) return null;
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                using (Icon ic = new Icon(path, 32, 32))
                    return WinUtil.Scale(ic.ToBitmap(), 32);
            using (Bitmap b = new Bitmap(path))
                return WinUtil.Scale(new Bitmap(b), 32);
        }

        private static string RegistryName(string aumid)
        {
            string n = RegValue(@"Software\Classes\AppUserModelId\" + aumid, "DisplayName");
            return string.IsNullOrEmpty(n) || n.StartsWith("@") ? null : n;   // "@..." 是间接字符串，不解析
        }

        // ---- 匹配窗口（UI 线程） ----

        // 窗口 h 是否属于这条通知的来源应用（按 exe 路径或包名）
        public static bool WindowMatches(IntPtr h, ToastSource s)
        {
            if (h == IntPtr.Zero || s == null) return false;
            if (s.ExePath == null && s.ExeName == null && s.Pfn == null) return false;
            IntPtr root = WinUtil.RootOwner(h);
            if (ProcessMatches(WinUtil.ProcessId(root), s)) return true;
            if (root != h && ProcessMatches(WinUtil.ProcessId(h), s)) return true;
            // UWP：顶层是 ApplicationFrameHost 的框架窗口，应用自己的窗口是它的子窗口
            if (s.Pfn != null && ClassName(root) == "ApplicationFrameWindow") return ChildMatches(root, s);
            return false;
        }

        private static bool ProcessMatches(uint pid, ToastSource s)
        {
            if (pid == 0) return false;
            if (s.ExePath != null || s.ExeName != null)
            {
                string exe = WinUtil.ExePath(pid);
                if (exe != null)
                {
                    if (s.ExePath != null && string.Equals(exe, s.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.ExeName != null && exe.StartsWith(s.ExeDir, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetFileName(exe), s.ExeName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            if (s.Pfn != null)
            {
                string pfn = PackageFamily(pid);
                if (pfn != null && string.Equals(pfn, s.Pfn, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool ChildMatches(IntPtr parent, ToastSource s)
        {
            bool hit = false;
            EnumChildWindows(parent, delegate(IntPtr c, IntPtr l)
            {
                if (!ProcessMatches(WinUtil.ProcessId(c), s)) return true;
                hit = true;
                return false;
            }, IntPtr.Zero);
            return hit;
        }

        // ---- 左键（UI 线程） ----

        // 先切到来源程序已有的窗口；没有就从「所有应用」启动它（单实例程序会调出自己的窗口）；
        // 再不行就打开通知中心——原通知还在那里，直接点它即可
        public static bool Activate(ToastSource s)
        {
            IntPtr h = FindAppWindow(s);
            if (h != IntPtr.Zero)
            {
                if (WinActivate.Activate(h)) return true;
                Log.Info("toast-activate: window 0x" + h.ToString("X") + " refused foreground");
            }
            if (s.InAppsFolder && SafeAumid(s.Aumid))
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (Launch(explorer, "\"" + AppsFolder(s.Aumid) + "\"")) return true;
            }
            return Launch("ms-actioncenter:", null);
        }

        private static IntPtr FindAppWindow(ToastSource s)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!IsAppWindow(h) || WinUtil.ProcessId(h) == MyPid || !WindowMatches(h, s)) return true;
                    found = h;
                    return false;   // EnumWindows 按 Z 序从上往下，第一个就是最近用过的
                }
                catch (Exception ex) { Log.Error("FindAppWindow", ex); return true; }
            }, IntPtr.Zero);
            return found;
        }

        // 任务栏 / Alt+Tab 意义上的应用窗口：可见、非工具窗口、无 owner（或显式 APPWINDOW）、没被 cloak
        private static bool IsAppWindow(IntPtr h)
        {
            if (!N.IsWindowVisible(h)) return false;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & N.WS_EX_APPWINDOW) == 0)
            {
                if ((ex & N.WS_EX_TOOLWINDOW) != 0) return false;
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
            }
            int cloaked;
            return !(DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0);
        }

        private static bool Launch(string file, string args)
        {
            try
            {
                N.AllowSetForegroundWindow(-1);   // ASFW_ANY：把这次点击得到的前台权限让给被启动的程序
                ProcessStartInfo psi = new ProcessStartInfo(file);
                if (args != null) psi.Arguments = args;
                psi.UseShellExecute = true;
                using (Process p = Process.Start(psi)) { }
                Log.Info("toast-launch " + file + (args == null ? "" : " " + args));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("toast-launch " + file, ex);
                return false;
            }
        }

        // ---- 工具 ----

        private static string AppsFolder(string aumid)
        {
            return @"shell:AppsFolder\" + aumid;
        }

        // AUMID 来自别的程序，拼进 shell 路径 / 命令行前先挡掉引号、控制字符、.. 之类
        private static bool SafeAumid(string aumid)
        {
            if (string.IsNullOrEmpty(aumid) || aumid.Length > 400 || aumid.Contains("..")) return false;
            foreach (char c in aumid)
                if (c < 0x20 || c == '"' || c == '<' || c == '>' || c == '|' || c == '*' || c == '?') return false;
            return true;
        }

        private static bool IsExe(string p)
        {
            return !string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        // 取 flag 后面的参数值（支持带引号）
        private static string ArgAfter(string args, string flag)
        {
            if (string.IsNullOrEmpty(args)) return null;
            int i = args.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            string rest = args.Substring(i + flag.Length).TrimStart(' ', '=');
            if (rest.StartsWith("\""))
            {
                int end = rest.IndexOf('"', 1);
                return end > 1 ? rest.Substring(1, end - 1) : null;
            }
            int sp = rest.IndexOf(' ');
            return sp < 0 ? rest : rest.Substring(0, sp);
        }

        // 命令行里的 exe 路径："C:\x\a.exe" -arg 或 C:\x\a.exe -arg
        private static string FirstExe(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                return end > 1 ? cmd.Substring(1, end - 1) : null;
            }
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? null : cmd.Substring(0, i + 4);
        }

        private static string RegValue(string key, string name)
        {
            foreach (RegistryKey root in new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using (RegistryKey k = root.OpenSubKey(key, false))
                {
                    object v = k == null ? null : k.GetValue(name);
                    if (v != null) return v.ToString();
                }
            }
            return null;
        }

        private static string KnownFolder(Guid g)
        {
            IntPtr p = IntPtr.Zero;
            try { return SHGetKnownFolderPath(g, 0, IntPtr.Zero, out p) == 0 ? Marshal.PtrToStringUni(p) : null; }
            finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }   // 失败时也要释放
        }

        private static string PackageFamily(uint pid)
        {
            IntPtr hp = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hp == IntPtr.Zero) return null;
            try
            {
                uint len = 0;
                if (GetPackageFamilyName(hp, ref len, null) != ERROR_INSUFFICIENT_BUFFER || len == 0) return null;   // 非打包进程返回 15700
                StringBuilder sb = new StringBuilder((int)len);
                return GetPackageFamilyName(hp, ref len, sb) == 0 ? sb.ToString() : null;
            }
            finally { N.CloseHandle(hp); }
        }

        private static string ClassName(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetString(IShellItem2 item, PROPERTYKEY key)
        {
            IntPtr p;
            if (item.GetString(ref key, out p) != 0 || p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        // ---- Win32 / Shell 声明 ----

        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
        private const uint SHGFI_LARGEICON = 0x0, SHGFI_PIDL = 0x8, SHGFI_ICON = 0x100, SHGFI_DISPLAYNAME = 0x200;

        private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
        private static readonly PROPERTYKEY PKEY_Link_TargetParsingPath = new PROPERTYKEY("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25", 2);
        private static readonly PROPERTYKEY PKEY_Link_Arguments = new PROPERTYKEY("436F2667-14E2-4FEB-B30A-146C53B5B674", 100);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
            public PROPERTYKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        // 方法顺序必须与 shobjidl_core.h 的 vtable 一致（IShellItem 5 个 + IShellItem2 13 个）
        [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem2
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IntPtr ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int Update(IntPtr pbc);
            [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
            [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
            [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long pft);
            [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int pi);
            [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr ppsz);
            [PreserveSig] int GetUInt32(ref PROPERTYKEY key, out uint pui);
            [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
            [PreserveSig] int GetBool(ref PROPERTYKEY key, out int pf);
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem2 item);
        [DllImport("shell32.dll")]
        private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, out IntPtr pidl);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attrs, ref SHFILEINFO sfi, uint cb, uint flags);
        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFamilyName(IntPtr hProcess, ref uint length, StringBuilder name);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    }
}
