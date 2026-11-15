using System;
using System.Collections.Generic;
using System.Drawing;

namespace FlashBar
{
    internal sealed class AttentionItem
    {
        public IntPtr Hwnd;        // 闪烁条目：请求关注的窗口；通知条目为 Zero
        public IntPtr Root;
        public uint Pid;
        public DateTime LastFlash;
        public string AppName;
        public string Title;
        public Bitmap Icon;
        public ToastInfo Toast;    // 非 null：接管的系统通知（实验功能）
    }

    internal enum FlashResult { Ignored, Updated, Expand }

    // 待处理列表（请求关注的窗口 + 接管的系统通知），最新的在末尾
    internal sealed class AttentionList
    {
        // HSHELL_FLASH 在闪烁期间会随每次闪亮重复发送；间隔超过这个秒数才算"新的一次闪烁"
        private const double RenewSeconds = 15;

        private readonly List<AttentionItem> _items = new List<AttentionItem>();
        // 被用户点掉/忽略的窗口 -> 最近一次收到 HSHELL_FLASH 的时间
        private readonly Dictionary<IntPtr, DateTime> _dismissed = new Dictionary<IntPtr, DateTime>();

        public int Count { get { return _items.Count; } }

        public int ToastCount
        {
            get
            {
                int n = 0;
                foreach (AttentionItem it in _items) if (it.Toast != null) n++;
                return n;
            }
        }

        public int FlashCount { get { return _items.Count - ToastCount; } }

        public AttentionItem Latest
        {
            get { return _items.Count == 0 ? null : _items[_items.Count - 1]; }
        }

        public FlashResult AddOrRefresh(IntPtr h)
        {
            DateTime now = DateTime.Now;
            DateTime last;
            if (_dismissed.TryGetValue(h, out last))
            {
                if ((now - last).TotalSeconds < RenewSeconds) { _dismissed[h] = now; return FlashResult.Ignored; }
                _dismissed.Remove(h);
            }
            // 同一程序已有通知条目：这次闪烁和那条通知是同一件事（聊天软件常常既闪任务栏又发通知），不再单独提醒
            if (HasToastFor(h)) { MarkDismissed(h, now); return FlashResult.Ignored; }
            AttentionItem it = Find(h);
            if (it != null)
            {
                bool renewed = (now - it.LastFlash).TotalSeconds >= RenewSeconds;
                it.LastFlash = now;
                it.Title = WinUtil.Title(h);
                if (it.Icon == null) it.Icon = WinUtil.GetIcon(h, it.Pid, 32);
                if (!renewed) return FlashResult.Updated;
                _items.Remove(it);
                _items.Add(it);
                return FlashResult.Expand;
            }
            it = new AttentionItem();
            it.Hwnd = h;
            it.Root = WinUtil.RootOwner(h);
            it.Pid = WinUtil.ProcessId(h);
            it.LastFlash = now;
            it.AppName = WinUtil.AppName(it.Pid);
            it.Title = WinUtil.Title(h);
            it.Icon = WinUtil.GetIcon(h, it.Pid, 32);
            _items.Add(it);
            Log.Info("flash: " + it.AppName + " | " + it.Title + " | 0x" + h.ToString("X"));
            return FlashResult.Expand;
        }

        public AttentionItem Find(IntPtr h)
        {
            foreach (AttentionItem it in _items) if (it.Toast == null && it.Hwnd == h) return it;
            return null;
        }

        // 用户点击/忽略：移出列表；闪烁条目在本轮闪烁结束前不再加入
        public bool Dismiss(AttentionItem it)
        {
            int i = _items.IndexOf(it);
            if (i < 0) return false;
            if (it.Toast == null) MarkDismissed(it.Hwnd, it.LastFlash);
            RemoveAt(i);
            return true;
        }

        // 窗口被激活：移除与 h 同一 root owner 的闪烁条目
        public bool RemoveRelated(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            IntPtr root = WinUtil.RootOwner(h);
            if (_dismissed.Count > 0)
            {
                List<IntPtr> keys = new List<IntPtr>(_dismissed.Keys);
                foreach (IntPtr k in keys)
                    if (k == h || k == root || WinUtil.RootOwner(k) == root) _dismissed.Remove(k);
            }
            bool changed = false;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                AttentionItem it = _items[i];
                if (it.Toast != null) continue;
                if (it.Hwnd == h || it.Root == h || it.Root == root || it.Hwnd == root)
                {
                    RemoveAt(i);
                    changed = true;
                }
            }
            return changed;
        }

        public bool RemoveDestroyed(IntPtr h)
        {
            _dismissed.Remove(h);
            AttentionItem it = Find(h);
            if (it == null) return false;
            RemoveAt(_items.IndexOf(it));
            return true;
        }

        // 巡检：失效/隐藏窗口、当前前台窗口对应的条目；顺便刷新标题
        public bool Sweep()
        {
            bool changed = false;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Toast != null) continue;
                IntPtr h = _items[i].Hwnd;
                if (!NativeMethods.IsWindow(h) || !WinUtil.IsShown(h)) { RemoveAt(i); changed = true; }
            }
            if (_dismissed.Count > 0)
            {
                List<IntPtr> keys = new List<IntPtr>(_dismissed.Keys);
                foreach (IntPtr k in keys) if (!NativeMethods.IsWindow(k)) _dismissed.Remove(k);
            }
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && RemoveRelated(fg)) changed = true;
            if (ToastAppActivated(fg)) changed = true;
            foreach (AttentionItem it in _items)
            {
                if (it.Toast != null) continue;
                string t = WinUtil.Title(it.Hwnd);
                if (t != it.Title) { it.Title = t; changed = true; }
            }
            return changed;
        }

        // ---- 系统通知条目（实验功能「接管系统通知」） ----

        public void AddToast(ToastInfo t)
        {
            DateTime now = DateTime.Now;
            // 同一程序先到的闪烁条目并进这条通知（通知更有信息量），本轮闪烁也不再单独提醒
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                AttentionItem f = _items[i];
                if (f.Toast != null || !ToastApps.WindowMatches(f.Hwnd, t.Source)) continue;
                MarkDismissed(f.Hwnd, now);
                RemoveAt(i);
            }
            AttentionItem it = new AttentionItem();
            it.Toast = t;
            it.LastFlash = now;
            it.AppName = t.Source.Name;
            it.Title = t.Text;
            it.Icon = t.Icon;
            t.Icon = null;
            // 到达时来源程序就在前台：等它离开前台、再被切回来，才算"看过了"
            t.Away = !ToastApps.WindowMatches(NativeMethods.GetForegroundWindow(), t.Source);
            _items.Add(it);
            // 只记来源和 Id，不把通知内容写进日志
            Log.Info("toast: " + it.AppName + " | " + (t.Source.Aumid ?? "?") + " | id=" + t.Id);
        }

        // 通知从通知中心消失了（在那边点掉 / 清除，或应用自己撤回）
        public bool RemoveToast(uint id)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ToastInfo t = _items[i].Toast;
                if (t != null && t.Id == id) { RemoveAt(i); return true; }
            }
            return false;
        }

        // aumid 为 null 时移除全部通知条目
        public bool RemoveToasts(string aumid)
        {
            bool changed = false;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ToastInfo t = _items[i].Toast;
                if (t == null) continue;
                if (aumid != null && !string.Equals(t.Source.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) continue;
                RemoveAt(i);
                changed = true;
            }
            return changed;
        }

        // 窗口 h 到了前台：来源程序在通知到达后离开过前台、现在又被切回来，视为已经看过
        public bool ToastAppActivated(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            bool changed = false;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                ToastInfo t = _items[i].Toast;
                if (t == null) continue;
                if (!ToastApps.WindowMatches(h, t.Source)) t.Away = true;
                else if (t.Away) { RemoveAt(i); changed = true; }
            }
            return changed;
        }

        private bool HasToastFor(IntPtr h)
        {
            foreach (AttentionItem it in _items)
                if (it.Toast != null && ToastApps.WindowMatches(h, it.Toast.Source)) return true;
            return false;
        }

        private void MarkDismissed(IntPtr h, DateTime at)
        {
            if (_dismissed.Count > 100) _dismissed.Clear();
            _dismissed[h] = at;
        }

        private void RemoveAt(int i)
        {
            AttentionItem it = _items[i];
            _items.RemoveAt(i);
            if (it.Icon != null) { it.Icon.Dispose(); it.Icon = null; }
        }
    }
}
