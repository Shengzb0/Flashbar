using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace FlashBar
{
    // 运行时按名字加载 WinRT 类型并反射调用（与 Windows PowerShell 5 读写通知是同一套机制）。
    // 好处：build.bat 不必引用 Windows SDK 的 winmd，系统自带 csc 照样能编译；
    // 系统不支持时只会让实验功能报「不可用」，不影响主功能
    internal static class WinRT
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        private static readonly Dictionary<Tuple<Type, string>, PropertyInfo> Props = new Dictionary<Tuple<Type, string>, PropertyInfo>();
        private static readonly Dictionary<Tuple<Type, string, int>, MethodInfo> Methods = new Dictionary<Tuple<Type, string, int>, MethodInfo>();
        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static MethodInfo _asTask;   // WindowsRuntimeSystemExtensions.AsTask<T>(IAsyncOperation<T>)

        // "Windows.UI.Notifications.UserNotification" -> Type
        public static Type T(string fullName)
        {
            lock (Gate)
            {
                Type t;
                if (Types.TryGetValue(fullName, out t)) return t;
                int dot = fullName.LastIndexOf('.');
                string ns = dot > 0 ? fullName.Substring(0, dot) : fullName;
                // 程序集名只是占位（ContentType=WindowsRuntime 才是关键），CLR 按类型名到 System32\WinMetadata 里找 winmd
                try { t = Type.GetType(fullName + ", " + ns + ", ContentType=WindowsRuntime", false); }
                catch (Exception) { t = null; }
                if (t == null) t = Type.GetType(fullName + ", Windows, ContentType=WindowsRuntime", true);
                Types[fullName] = t;
                return t;
            }
        }

        public static object Enum(string typeName, int value)
        {
            return System.Enum.ToObject(T(typeName), value);
        }

        public static object StaticGet(string typeName, string prop)
        {
            PropertyInfo p = T(typeName).GetProperty(prop, BindingFlags.Public | BindingFlags.Static);
            if (p == null) throw new MissingMemberException(typeName, prop);
            return p.GetValue(null, null);
        }

        public static object Get(object obj, string prop)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            Type t = obj.GetType();
            Tuple<Type, string> key = Tuple.Create(t, prop);
            PropertyInfo p;
            lock (Gate)
            {
                if (!Props.TryGetValue(key, out p))
                {
                    p = t.GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                    Props[key] = p;
                }
            }
            if (p == null) throw new MissingMemberException(t.FullName, prop);
            return p.GetValue(obj, null);
        }

        public static void Set(object obj, string prop, object value)
        {
            PropertyInfo p = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) throw new MissingMemberException(obj.GetType().FullName, prop);
            p.SetValue(obj, value, null);
        }

        // 读不到返回 null：单条通知的个别字段坏了不影响其余字段（同一字段只记一次日志）
        public static object TryGet(object obj, string prop)
        {
            if (obj == null) return null;
            try { return Get(obj, prop); }
            catch (Exception ex) { LogOnce(prop, ex); return null; }
        }

        // 按名字和参数个数找实例方法（WinRT 方法很少按类型重载）；轮询时每秒调好几次，所以缓存
        public static object Call(object obj, string name, params object[] args)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            Type t = obj.GetType();
            Tuple<Type, string, int> key = Tuple.Create(t, name, args.Length);
            MethodInfo found;
            lock (Gate)
            {
                if (!Methods.TryGetValue(key, out found))
                {
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (m.Name == name && m.GetParameters().Length == args.Length) { found = m; break; }
                    Methods[key] = found;
                }
            }
            if (found == null) throw new MissingMethodException(t.FullName, name);
            return found.Invoke(obj, args);
        }

        // 阻塞等待 IAsyncOperation<T>，只在后台线程用
        public static object Await(object op, Type resultType, int timeoutMs)
        {
            Task task = AsTask(op, resultType);
            if (!task.Wait(timeoutMs)) throw new TimeoutException("WinRT call timed out");
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        // 提前确认异步桥接可用，失败就直接判「不可用」，而不是等轮询反复出错
        public static void Prepare()
        {
            AsTaskMethod();
        }

        public static Task AsTask(object op, Type resultType)
        {
            return (Task)AsTaskMethod().MakeGenericMethod(resultType).Invoke(null, new object[] { op });
        }

        // WinRT 集合（IVectorView 等）拷成普通列表
        public static List<object> ToList(object coll, Type elem)
        {
            List<object> r = new List<object>();
            if (coll == null) return r;
            IEnumerable e = coll as IEnumerable;
            if (e != null)
            {
                try
                {
                    foreach (object o in e) r.Add(o);
                    return r;
                }
                catch (Exception ex) { LogOnce("enumerate", ex); r.Clear(); }
            }
            if (elem == null) return r;
            // 退路：按 IReadOnlyList<T> 的 Count / 索引器逐个取
            int n = (int)typeof(IReadOnlyCollection<>).MakeGenericType(elem).GetProperty("Count").GetValue(coll, null);
            PropertyInfo item = typeof(IReadOnlyList<>).MakeGenericType(elem).GetProperty("Item");
            for (int i = 0; i < n; i++) r.Add(item.GetValue(coll, new object[] { i }));
            return r;
        }

        public static void LogOnce(string what, Exception ex)
        {
            lock (Gate) { if (!Logged.Add(what)) return; }
            Log.Error("WinRT " + what, Inner(ex));
        }

        // 剥掉反射 / Task 套的壳，拿到真正的异常
        public static Exception Inner(Exception ex)
        {
            while (ex != null)
            {
                TargetInvocationException tie = ex as TargetInvocationException;
                if (tie != null && tie.InnerException != null) { ex = tie.InnerException; continue; }
                AggregateException ae = ex as AggregateException;
                if (ae != null && ae.InnerException != null) { ex = ae.InnerException; continue; }
                break;
            }
            return ex;
        }

        private static MethodInfo AsTaskMethod()
        {
            lock (Gate)
            {
                if (_asTask != null) return _asTask;
                Assembly asm = null;
                try { asm = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"); }
                catch (Exception ex) { Log.Error("WinRT load System.Runtime.WindowsRuntime", ex); }
#pragma warning disable 618
                if (asm == null) asm = Assembly.LoadWithPartialName("System.Runtime.WindowsRuntime");
#pragma warning restore 618
                if (asm == null) throw new InvalidOperationException("System.Runtime.WindowsRuntime not found");
                Type ext = asm.GetType("System.WindowsRuntimeSystemExtensions", true);
                foreach (MethodInfo m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "AsTask" || !m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.Name == "IAsyncOperation`1") { _asTask = m; break; }
                }
                if (_asTask == null) throw new MissingMethodException("System.WindowsRuntimeSystemExtensions", "AsTask");
                return _asTask;
            }
        }
    }
}
