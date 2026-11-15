using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FlashBar
{
    // 一条被接管的系统通知
    internal sealed class ToastInfo
    {
        public uint Id;
        public ToastSource Source;
        public string Text;         // 「标题：正文」
        public DateTime Created;
        public Bitmap Icon;         // 交给 AttentionItem 后置 null
        public bool Away;           // 到达后来源程序离开过前台（再切回去才算"看过了"）
        public bool IsTest;         // 托盘「发送测试通知」发出的那条
    }

    internal delegate void ToastHandler(ToastWatcher sender, ToastInfo t);
    internal delegate void ToastGoneHandler(ToastWatcher sender, uint id);
    internal delegate void ToastFailHandler(ToastWatcher sender, string reason);

    // 实验功能「接管系统通知」：轮询通知中心（UserNotificationListener）。
    // 非打包程序订阅 NotificationChanged 会报 0x80070490，所以只能轮询：首轮记基线，之后按 Id 比对新增 / 消失
    internal sealed class ToastWatcher : IDisposable
    {
        public const int Unsupported = -1, Unspecified = 0, Allowed = 1, Denied = 2;

        // Windows PowerShell 的 AUMID：系统自带、常被脚本拿来发通知；FlashBar 没有自己的 AUMID，测试通知借用它
        public const string TestAumid = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

        private const string ListenerType = "Windows.UI.Notifications.Management.UserNotificationListener";
        private const string StatusType = "Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus";
        private const string NotificationType = "Windows.UI.Notifications.UserNotification";
        private const string KindsType = "Windows.UI.Notifications.NotificationKinds";
        private const string ManagerType = "Windows.UI.Notifications.ToastNotificationManager";
        private const string TestTitle = "FlashBar 测试通知";
        private const string TestTag = "flashbar-test", TestGroup = "flashbar";
        private const int PollMs = 500;
        private const int AccessCheckMs = 5000;
        private const int CallTimeoutMs = 5000;
        private const double MaxAgeSeconds = 120;   // 新出现但早就创建了的（服务重启、迟到的同步）只记不弹
        private const int MaxBurst = 20;            // 一次冒出太多"新"通知视为重新同步，只记不弹
        private const int MaxFailures = 10;
        private const int MaxText = 200;

        public event ToastHandler Arrived;
        public event ToastGoneHandler Gone;
        public event ToastFailHandler Failed;

        private readonly SynchronizationContext _ui;
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private readonly Thread _thread;
        private readonly int _session;
        // AUMID -> 来源应用，仅监听线程访问
        private readonly Dictionary<string, ToastSource> _sources = new Dictionary<string, ToastSource>(StringComparer.OrdinalIgnoreCase);
        private bool _stopped;   // 仅 UI 线程读写

        public ToastWatcher(int bannerSession)
        {
            _session = bannerSession;
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "FlashBar.Toasts";
            // shell 解析（图标、开始菜单快捷方式的目标）按惯例在 STA 里做；监听器是 Agile 对象，哪个套间都能调
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public void Start()
        {
            _thread.Start();
        }

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            _stopEvent.Set();
            // 线程卡在一次系统调用里时不死等界面，它醒来看到停止信号会自己退出
            if (_thread.IsAlive && !_thread.Join(1000)) Log.Info("toast watcher: still busy, it will exit on its own");
        }

        // ---- 授权（UI 线程调用） ----

        // 当前授权状态；Unsupported = 系统没有这个 API 或 WinRT 不可用
        public static int QueryAccess(out string error)
        {
            error = null;
            try
            {
                WinRT.Prepare();
                object listener = WinRT.StaticGet(ListenerType, "Current");
                return Convert.ToInt32(WinRT.Call(listener, "GetAccessStatus"));
            }
            catch (Exception ex)
            {
                ex = WinRT.Inner(ex);
                error = ex.GetType().Name + ": " + ex.Message;
                Log.Error("Toast.QueryAccess", ex);
                return Unsupported;
            }
        }

        // 请求授权（文档要求在 UI 线程调用），结果回到 UI 线程
        public static void RequestAccess(Action<int> done)
        {
            SynchronizationContext ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            Task task;
            try
            {
                object listener = WinRT.StaticGet(ListenerType, "Current");
                task = WinRT.AsTask(WinRT.Call(listener, "RequestAccessAsync"), WinRT.T(StatusType));
            }
            catch (Exception ex)
            {
                Log.Error("Toast.RequestAccess", WinRT.Inner(ex));
                done(Unsupported);
                return;
            }
            task.ContinueWith(delegate(Task t)
            {
                int r = Denied;
                try
                {
                    if (t.IsFaulted || t.IsCanceled) Log.Error("Toast.RequestAccess", t.Exception);
                    else r = Convert.ToInt32(t.GetType().GetProperty("Result").GetValue(t, null));
                }
                catch (Exception ex) { Log.Error("Toast.RequestAccess.Result", ex); }
                ui.Post(delegate { done(r); }, null);
            });
        }

        // 以 Windows PowerShell 的身份发一条测试通知（托盘「发送测试通知」）。
        // 固定 tag/group：新的测试通知会顶掉旧的，通知中心里最多留一条
        public static void ShowTest()
        {
            string body = "接管成功时它会出现在底部胶囊里 · " + DateTime.Now.ToString("HH:mm:ss");
            string xml = "<toast><visual><binding template=\"ToastGeneric\"><text>" + TestTitle + "</text><text>"
                + SecurityElement.Escape(body) + "</text></binding></visual></toast>";
            object doc = Activator.CreateInstance(WinRT.T("Windows.Data.Xml.Dom.XmlDocument"));
            WinRT.Call(doc, "LoadXml", xml);
            object toast = Activator.CreateInstance(WinRT.T("Windows.UI.Notifications.ToastNotification"), doc);
            WinRT.Set(toast, "Tag", TestTag);
            WinRT.Set(toast, "Group", TestGroup);
            MethodInfo create = WinRT.T(ManagerType).GetMethod("CreateToastNotifier", new Type[] { typeof(string) });
            object notifier = create.Invoke(null, new object[] { TestAumid });
            WinRT.Call(notifier, "Show", toast);
        }

        // 测试通知点掉 / 忽略后，顺手从通知中心删掉（它只是个测试，不留痕）
        public static void ClearTest()
        {
            try
            {
                object history = WinRT.StaticGet(ManagerType, "History");
                WinRT.Call(history, "Remove", TestTag, TestGroup, TestAumid);
            }
            catch (Exception ex) { Log.Error("Toast.ClearTest", WinRT.Inner(ex)); }
        }

        // ---- 监听线程 ----

        private void Run()
        {
            try { Loop(); }
            catch (Exception ex)
            {
                Log.Error("ToastWatcher", WinRT.Inner(ex));
                Fail("出错");
            }
            finally
            {
                try
                {
                    foreach (ToastSource s in _sources.Values)
                        if (s.Icon != null) { s.Icon.Dispose(); s.Icon = null; }
                    _sources.Clear();
                }
                catch (Exception ex) { Log.Error("ToastWatcher.Cleanup", ex); }
            }
        }

        private void Loop()
        {
            object listener, kinds;
            Type itemType, listType;
            try
            {
                WinRT.Prepare();
                listener = WinRT.StaticGet(ListenerType, "Current");
                kinds = WinRT.Enum(KindsType, 1);   // NotificationKinds.Toast
                itemType = WinRT.T(NotificationType);
                listType = typeof(IReadOnlyList<>).MakeGenericType(itemType);
            }
            catch (Exception ex)
            {
                Log.Error("ToastWatcher.Init", WinRT.Inner(ex));
                Fail("不可用");
                return;
            }

            HashSet<uint> seen = null;   // null = 还没建基线
            int failures = 0, wait = 0;
            DateTime accessDue = DateTime.MinValue;
            while (!_stopEvent.WaitOne(wait))
            {
                wait = PollMs;
                List<object> list;
                try
                {
                    if (DateTime.UtcNow >= accessDue)
                    {
                        accessDue = DateTime.UtcNow.AddMilliseconds(AccessCheckMs);
                        // 权限被收回时 API 不报错、只返回空列表；不主动查就会在横幅关着的情况下什么都收不到
                        int st = Convert.ToInt32(WinRT.Call(listener, "GetAccessStatus"));
                        if (st != Allowed)
                        {
                            Log.Info("toast access lost, status=" + st);
                            Fail("未授权");
                            return;
                        }
                    }
                    object result = WinRT.Await(WinRT.Call(listener, "GetNotificationsAsync", kinds), listType, CallTimeoutMs);
                    list = WinRT.ToList(result, itemType);
                    failures = 0;
                }
                catch (Exception ex)
                {
                    failures++;
                    if (failures <= 3 || failures == MaxFailures) Log.Error("ToastWatcher.Poll#" + failures, WinRT.Inner(ex));
                    if (failures >= MaxFailures) { Fail("出错"); return; }
                    wait = Math.Min(PollMs << failures, 8000);
                    continue;
                }
                if (_stopEvent.WaitOne(0)) return;
                try { seen = Diff(list, seen); }
                catch (Exception ex) { Log.Error("ToastWatcher.Diff", WinRT.Inner(ex)); }
            }
        }

        private HashSet<uint> Diff(List<object> list, HashSet<uint> seen)
        {
            HashSet<uint> now = new HashSet<uint>();
            List<object> fresh = new List<object>();
            foreach (object un in list)
            {
                uint id;
                try { id = Convert.ToUInt32(WinRT.Get(un, "Id")); }
                catch (Exception ex) { WinRT.LogOnce("UserNotification.Id", ex); continue; }
                if (!now.Add(id)) continue;
                if (seen == null || !seen.Contains(id)) fresh.Add(un);
            }

            if (seen == null)
            {
                // 首轮：已有通知只记基线不弹；它们的来源应用一并纳入接管，提前关掉横幅
                foreach (object un in fresh)
                {
                    try
                    {
                        ToastSource s = ReadSource(un);
                        ToastBanner.Register(s.Aumid, s.Name);
                    }
                    catch (Exception ex) { WinRT.LogOnce("baseline", ex); }
                }
                ToastBanner.Activate(_session);
                Log.Info("toast baseline: " + now.Count + " notification(s) in notification center");
                return now;
            }

            foreach (uint id in seen)
                if (!now.Contains(id)) PostGone(id);
            if (fresh.Count > MaxBurst)
            {
                Log.Info("toast: " + fresh.Count + " new ids at once, treated as resync");
                return now;
            }
            List<ToastInfo> items = new List<ToastInfo>();
            foreach (object un in fresh)
            {
                try
                {
                    ToastInfo t = Read(un);
                    if (t != null) items.Add(t);
                }
                catch (Exception ex) { Log.Error("ToastWatcher.Read", WinRT.Inner(ex)); }
            }
            items.Sort(delegate(ToastInfo a, ToastInfo b) { return a.Created.CompareTo(b.Created); });
            foreach (ToastInfo t in items) PostArrived(t);
            return now;
        }

        private ToastInfo Read(object un)
        {
            ToastInfo t = new ToastInfo();
            t.Id = Convert.ToUInt32(WinRT.Get(un, "Id"));
            object created = WinRT.TryGet(un, "CreationTime");
            t.Created = created is DateTimeOffset ? ((DateTimeOffset)created).LocalDateTime : DateTime.Now;
            if ((DateTime.Now - t.Created).TotalSeconds > MaxAgeSeconds)
            {
                Log.Info("toast id=" + t.Id + " created " + t.Created.ToString("HH:mm:ss") + ", too old, skipped");
                return null;
            }
            ToastSource s = ReadSource(un);
            // 系统通知 / 已交还系统的应用：完全交给 Windows（照常弹横幅，FlashBar 不显示）
            if (!ToastBanner.Take(s.Aumid, s.Name, _session)) return null;
            t.Source = s;
            t.Text = ReadText(un, s.Name);
            t.IsTest = string.Equals(s.Aumid, TestAumid, StringComparison.OrdinalIgnoreCase) && t.Text.StartsWith(TestTitle, StringComparison.Ordinal);
            if (s.Icon != null) t.Icon = new Bitmap(s.Icon);
            return t;
        }

        // 来源应用：按 AUMID 解析一次（名称、图标、exe），之后复用
        private ToastSource ReadSource(object un)
        {
            object app = WinRT.TryGet(un, "AppInfo");
            string aumid = WinRT.TryGet(app, "AppUserModelId") as string;
            string key = aumid ?? "";
            ToastSource s;
            if (_sources.TryGetValue(key, out s)) return s;
            s = new ToastSource();
            s.Aumid = string.IsNullOrEmpty(aumid) ? null : aumid;
            s.Pfn = WinRT.TryGet(app, "PackageFamilyName") as string;
            if (string.IsNullOrEmpty(s.Pfn)) s.Pfn = null;
            s.Name = WinRT.TryGet(WinRT.TryGet(app, "DisplayInfo"), "DisplayName") as string;
            if (s.Aumid != null && !ToastBanner.IsSystem(s.Aumid)) ToastApps.Describe(s);   // 系统通知不接管，不用解析
            if (string.IsNullOrEmpty(s.Name)) s.Name = s.Aumid == null ? "通知" : ToastApps.ShortName(s.Aumid);
            _sources[key] = s;
            Log.Info("toast source: " + s.Name + " | " + (s.Aumid ?? "?") + " | exe=" + (s.ExePath ?? s.ExeName ?? "-")
                + " | pfn=" + (s.Pfn ?? "-") + " | appsfolder=" + s.InAppsFolder + " | icon=" + (s.Icon != null));
            return s;
        }

        // ToastGeneric 绑定的文本：第一行当标题，其余当正文
        private static string ReadText(object un, string appName)
        {
            object visual = WinRT.TryGet(WinRT.TryGet(un, "Notification"), "Visual");
            if (visual == null) return "";
            object binding = null;
            try { binding = WinRT.Call(visual, "GetBinding", "ToastGeneric"); }
            catch (Exception ex) { WinRT.LogOnce("GetBinding", ex); }
            if (binding == null)
                foreach (object b in WinRT.ToList(WinRT.TryGet(visual, "Bindings"), null)) { binding = b; break; }
            if (binding == null) return "";
            object texts = null;
            try { texts = WinRT.Call(binding, "GetTextElements"); }
            catch (Exception ex) { WinRT.LogOnce("GetTextElements", ex); }
            List<string> parts = new List<string>();
            foreach (object e in WinRT.ToList(texts, null))
            {
                string s = Clean(WinRT.TryGet(e, "Text") as string);
                if (s.Length > 0) parts.Add(s);
            }
            if (parts.Count == 0) return "";
            string title = parts[0];
            string body = string.Join(" ", parts.GetRange(1, parts.Count - 1).ToArray());
            string text;
            if (body.Length == 0) text = title;
            else if (string.Equals(title, appName, StringComparison.OrdinalIgnoreCase)) text = body;   // 标题就是应用名时不重复
            else text = title + "：" + body;
            return text.Length > MaxText ? text.Substring(0, MaxText) + "…" : text;
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().Trim();
        }

        // ---- 回到 UI 线程 ----

        private void PostArrived(ToastInfo t)
        {
            if (!Post(delegate
            {
                ToastHandler h = Arrived;
                if (_stopped || h == null)
                {
                    if (t.Icon != null) { t.Icon.Dispose(); t.Icon = null; }
                    return;
                }
                h(this, t);
            }) && t.Icon != null) { t.Icon.Dispose(); t.Icon = null; }
        }

        private void PostGone(uint id)
        {
            Post(delegate
            {
                ToastGoneHandler h = Gone;
                if (!_stopped && h != null) h(this, id);
            });
        }

        private void Fail(string reason)
        {
            Post(delegate
            {
                ToastFailHandler h = Failed;
                if (!_stopped && h != null) h(this, reason);
            });
        }

        // 程序正在退出时界面线程的消息窗口可能已销毁，Post 会抛异常；后台线程上的未处理异常会直接结束进程，必须兜住
        private bool Post(SendOrPostCallback cb)
        {
            try
            {
                _ui.Post(cb, null);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("ToastWatcher.Post", ex);
                return false;
            }
        }
    }
}
