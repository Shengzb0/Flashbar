using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FlashBar
{
    internal sealed class ToastApp
    {
        public string Aumid;
        public string Name;
        public bool Excluded;   // 交还系统：Windows 照常弹横幅，FlashBar 不显示
        public string Saved;    // null = 横幅没被 FlashBar 关；"-" = 关之前没有 ShowBanner 值；数字 = 关之前的值
    }

    // 接管横幅：写 HKCU\...\Notifications\Settings\<AUMID>\ShowBanner=0，
    // 等同「设置 → 系统 → 通知 → 某应用 → 显示通知横幅」关掉——通知照常进通知中心、照常能被读到，只是不在右下角弹。
    // 原值先写进 toast-apps.txt 再改注册表；关闭功能 / 退出 / 注销 / 下次启动对账时还原（只还原仍是 0 的，期间用户改过的不动）
    internal static class ToastBanner
    {
        private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\";
        private const string Value = "ShowBanner";

        // 留给 Windows 的：系统组件的通知常要当场操作（自动播放、蓝牙配对、更新重启、安全中心），闹钟 / 计时器必须照常弹出
        private static readonly string[] SystemPrefixes = { "Windows.", "Microsoft.Windows.", "MicrosoftWindows.", "Microsoft.WindowsAlarms_" };

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ToastApp> Apps = new Dictionary<string, ToastApp>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;
        private static bool _active;   // 正在接管：新来的应用顺手关横幅
        private static int _session;   // 每次开始 / 结束接管都 +1，旧监听线程晚到的调用不会再去关横幅

        private static string FilePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Log.FilePath), "toast-apps.txt"); }
        }

        public static bool IsSystem(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return false;
            foreach (string p in SystemPrefixes)
                if (aumid.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 开始一次接管（此时还不关横幅，等监听线程拿到基线后调 Activate）
        public static int Begin()
        {
            lock (Gate)
            {
                _active = false;
                return ++_session;
            }
        }

        // 监听线程建好基线后：把已登记、没交还系统的应用的横幅都关掉
        public static void Activate(int session)
        {
            lock (Gate)
            {
                if (session != _session) return;
                Load();
                _active = true;
                foreach (ToastApp a in Apps.Values)
                    if (!a.Excluded && !IsSystem(a.Aumid)) Suppress(a, false);
            }
        }

        // 结束接管：横幅全部还给系统
        public static void Deactivate()
        {
            lock (Gate)
            {
                _session++;
                _active = false;
                RestoreAllLocked();
            }
        }

        // 注销 / 关机时先还原（接管状态保留：若注销被取消，之后各应用来通知时会再关上）
        public static void RestoreAll()
        {
            lock (Gate) RestoreAllLocked();
        }

        // 基线里出现过的应用：只登记，不关横幅（由 Activate 统一处理）
        public static void Register(string aumid, string name)
        {
            if (!Registrable(aumid)) return;
            lock (Gate) RegisterLocked(aumid, name);
        }

        // 新通知到达：返回 FlashBar 是否显示它；接管中顺手关掉该应用的横幅
        public static bool Take(string aumid, string name, int session)
        {
            if (string.IsNullOrEmpty(aumid)) return true;   // 来源不明：照样显示，只是没法关它的横幅
            if (IsSystem(aumid)) return false;
            if (!Registrable(aumid)) return true;
            lock (Gate)
            {
                ToastApp a = RegisterLocked(aumid, name);
                if (a.Excluded) return false;
                if (_active && session == _session) Suppress(a, false);
                return true;
            }
        }

        // 「发送测试通知」前先接管测试用的身份，这样第一条测试通知就能看到效果
        // （该应用可能还没发过通知、系统还没建它的设置键，所以允许新建）
        public static void Adopt(string aumid, string name)
        {
            if (!Registrable(aumid)) return;
            lock (Gate)
            {
                ToastApp a = RegisterLocked(aumid, name);
                if (!a.Excluded && _active) Suppress(a, true);
            }
        }

        // 托盘「已接管的应用」里勾选 / 取消
        public static void SetExcluded(string aumid, bool excluded)
        {
            lock (Gate)
            {
                Load();
                ToastApp a;
                if (!Apps.TryGetValue(aumid, out a) || a.Excluded == excluded) return;
                a.Excluded = excluded;
                if (excluded) Restore(a);
                else if (_active) Suppress(a, false);
                Save();
                Log.Info("toast app " + aumid + (excluded ? " -> windows" : " -> flashbar"));
            }
        }

        public static List<ToastApp> Snapshot()
        {
            lock (Gate)
            {
                Load();
                List<ToastApp> r = new List<ToastApp>();
                foreach (ToastApp a in Apps.Values)
                {
                    ToastApp c = new ToastApp();
                    c.Aumid = a.Aumid;
                    c.Name = a.Name;
                    c.Excluded = a.Excluded;
                    c.Saved = a.Saved;
                    r.Add(c);
                }
                r.Sort(delegate(ToastApp x, ToastApp y)
                {
                    return string.Compare(x.Name ?? x.Aumid, y.Name ?? y.Aumid, StringComparison.CurrentCultureIgnoreCase);
                });
                return r;
            }
        }

        // ---- 以下都在 Gate 里调用 ----

        private static bool Registrable(string aumid)
        {
            return !string.IsNullOrEmpty(aumid) && !IsSystem(aumid)
                && aumid.IndexOf('\t') < 0 && aumid.IndexOf('\r') < 0 && aumid.IndexOf('\n') < 0;
        }

        private static ToastApp RegisterLocked(string aumid, string name)
        {
            Load();
            ToastApp a;
            if (!Apps.TryGetValue(aumid, out a))
            {
                a = new ToastApp();
                a.Aumid = aumid;
                a.Name = name;
                // 用户本来就关了这个应用的横幅（只想让它安静地进通知中心）：默认也不让 FlashBar 弹，需要时在菜单里勾上
                a.Excluded = BannerAlreadyOff(aumid);
                Apps[aumid] = a;
                Log.Info("toast app registered: " + aumid + (a.Excluded ? " (banner already off, left to windows)" : ""));
                Save();
            }
            else if (!string.IsNullOrEmpty(name) && a.Name != name)
            {
                a.Name = name;
                Save();
            }
            return a;
        }

        private static void RestoreAllLocked()
        {
            Load();
            bool dirty = false;
            foreach (ToastApp a in Apps.Values)
                if (Restore(a)) dirty = true;
            if (dirty) Save();
        }

        // 当前 ShowBanner：null = 没有这个值（默认弹横幅）
        private static int? Current(RegistryKey k)
        {
            object v = k.GetValue(Value);
            if (v is int) return (int)v;
            return v == null ? (int?)null : 1;   // 类型不对的值按"开"处理
        }

        private static bool BannerAlreadyOff(string aumid)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey + aumid, false))
                {
                    if (k == null) return false;
                    int? cur = Current(k);
                    return cur.HasValue && cur.Value == 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error("ToastBanner.BannerAlreadyOff " + aumid, ex);
                return false;
            }
        }

        // create=false 时只改已存在的设置键：系统在应用第一次发通知时就会建键，键不在通常说明应用已卸载，不去凭空造
        private static void Suppress(ToastApp a, bool create)
        {
            try
            {
                using (RegistryKey k = create
                    ? Registry.CurrentUser.CreateSubKey(SettingsKey + a.Aumid)
                    : Registry.CurrentUser.OpenSubKey(SettingsKey + a.Aumid, true))
                {
                    if (k == null) return;
                    int? cur = Current(k);
                    if (cur.HasValue && cur.Value == 0) return;   // 已经关着（我们关的，或用户本来就关了）
                    if (a.Saved == null)
                    {
                        // 先把原值存盘再改注册表：中途崩溃也能还原；存不下来就不改
                        a.Saved = cur.HasValue ? cur.Value.ToString() : "-";
                        if (!Save()) { a.Saved = null; return; }
                    }
                    k.SetValue(Value, 0, RegistryValueKind.DWord);
                }
                Log.Info("banner off: " + a.Aumid);
            }
            catch (Exception ex) { Log.Error("ToastBanner.Suppress " + a.Aumid, ex); }
        }

        private static bool Restore(ToastApp a)
        {
            if (a.Saved == null) return false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey + a.Aumid, true))
                {
                    if (k != null)
                    {
                        int? cur = Current(k);
                        if (cur.HasValue && cur.Value == 0)   // 仍是我们写的 0 才还原
                        {
                            if (a.Saved == "-") k.DeleteValue(Value, false);
                            else k.SetValue(Value, int.Parse(a.Saved), RegistryValueKind.DWord);
                        }
                    }
                }
                a.Saved = null;
                Log.Info("banner restored: " + a.Aumid);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("ToastBanner.Restore " + a.Aumid, ex);   // 备份留着，下次对账再试
                return false;
            }
        }

        // 每行：M|X <tab> 原值 <tab> AUMID <tab> 名称
        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 3 || f[2].Length == 0) continue;
                    ToastApp a = new ToastApp();
                    a.Excluded = f[0] == "X";
                    int n;
                    a.Saved = f[1] == "-" || int.TryParse(f[1], out n) ? f[1] : null;
                    a.Aumid = f[2];
                    a.Name = f.Length > 3 && f[3].Length > 0 ? f[3] : null;
                    Apps[a.Aumid] = a;
                }
            }
            catch (Exception ex) { Log.Error("ToastBanner.Load", ex); }
        }

        // 先写临时文件再替换，避免写到一半崩溃丢掉备份
        private static bool Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (ToastApp a in Apps.Values)
                {
                    sb.Append(a.Excluded ? "X" : "M").Append('\t').Append(a.Saved ?? "").Append('\t')
                      .Append(a.Aumid).Append('\t').Append(OneLine(a.Name)).Append("\r\n");
                }
                string p = FilePath, tmp = p + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (!File.Exists(p)) { File.Move(tmp, p); return true; }
                try { File.Replace(tmp, p, null); }
                catch (Exception)
                {
                    File.Copy(tmp, p, true);
                    File.Delete(tmp);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("ToastBanner.Save", ex);
                return false;
            }
        }

        private static string OneLine(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
