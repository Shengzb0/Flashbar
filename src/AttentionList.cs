using System;
using System.Collections.Generic;
using System.Drawing;

namespace FlashBar
{
    internal sealed class AttentionItem
    {
        public IntPtr Hwnd;
        public IntPtr Root;
        public uint Pid;
        public DateTime LastFlash;
        public string AppName;
        public string Title;
        public Bitmap Icon;
    }

    internal enum FlashResult { Ignored, Updated, Expand }

    // 待处理（请求关注）窗口列表，最新的在末尾
    internal sealed class AttentionList
    {
        // HSHELL_FLASH 在闪烁期间会随每次闪亮重复发送；间隔超过这个秒数才算"新的一次闪烁"
        private const double RenewSeconds = 15;

        private readonly List<AttentionItem> _items = new List<AttentionItem>();
        // 被用户点掉/忽略的窗口 -> 最近一次收到 HSHELL_FLASH 的时间
        private readonly Dictionary<IntPtr, DateTime> _dismissed = new Dictionary<IntPtr, DateTime>();

        public int Count { get { return _items.Count; } }

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
            foreach (AttentionItem it in _items) if (it.Hwnd == h) return it;
            return null;
        }

        // 用户点击/忽略：移出列表，并在本轮闪烁结束前不再加入
        public bool Dismiss(AttentionItem it)
        {
            int i = _items.IndexOf(it);
            if (i < 0) return false;
            if (_dismissed.Count > 100) _dismissed.Clear();
            _dismissed[it.Hwnd] = it.LastFlash;
            RemoveAt(i);
            return true;
        }

        // 窗口被激活：移除与 h 同一 root owner 的条目
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
            foreach (AttentionItem it in _items)
            {
                string t = WinUtil.Title(it.Hwnd);
                if (t != it.Title) { it.Title = t; changed = true; }
            }
            return changed;
        }

        private void RemoveAt(int i)
        {
            AttentionItem it = _items[i];
            _items.RemoveAt(i);
            if (it.Icon != null) { it.Icon.Dispose(); it.Icon = null; }
        }
    }
}
