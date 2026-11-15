using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using N = FlashBar.NativeMethods;

namespace FlashBar
{
    // 底部胶囊提醒：展开态 / 细线态
    internal sealed partial class PillForm : Form
    {
        public const string PillTitle = "FlashBarPill";
        private const double NewFlashSeconds = 8;
        private const double LeaveSeconds = 2;

        private readonly AttentionList _list;
        private readonly Timer _hover;
        private bool _expanded;
        private DateTime _collapseAt;
        private Rectangle _expRect;   // 展开态屏幕矩形
        private Rectangle _colRect;   // 细线态屏幕矩形

        public event EventHandler ActivateRequested;
        public event EventHandler DismissRequested;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        public PillForm(AttentionList list)
        {
            _list = list;
            Text = PillTitle;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            Size = new Size(1, 1);
            Location = new Point(-32000, -32000);
            _hover = new Timer();
            _hover.Interval = 200;
            _hover.Tick += OnHoverTick;
            IntPtr force = Handle;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= N.WS_EX_NOACTIVATE | N.WS_EX_TOOLWINDOW | N.WS_EX_TOPMOST | Layered.WS_EX_LAYERED;
                cp.ExStyle &= ~N.WS_EX_APPWINDOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == N.WM_MOUSEACTIVATE) { m.Result = (IntPtr)N.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
            // 缩放、工作区、分辨率变化：立即重新定位
            if (m.Msg == N.WM_DPICHANGED || m.Msg == WM_SETTINGCHANGE || m.Msg == WM_DISPLAYCHANGE)
            {
                try { Reposition(); }
                catch (Exception ex) { Log.Error("Reposition", ex); }
            }
        }

        // 列表变化后调用；expandNew=新闪烁需展开 8 秒
        public void UpdateView(bool expandNew)
        {
            if (_list.Count == 0) { HidePill(); return; }
            if (expandNew)
            {
                _expanded = true;
                _collapseAt = DateTime.Now.AddSeconds(NewFlashSeconds);
            }
            Render();
        }

        public void ExpandFor(double seconds)
        {
            if (_list.Count == 0) return;
            _expanded = true;
            _collapseAt = DateTime.Now.AddSeconds(seconds);
            Render();
        }

        // 分辨率/缩放变化后重新定位
        public void Reposition()
        {
            if (_list.Count > 0 && (Visible || _suppressed)) Render();
        }

        private void HidePill()
        {
            _hover.Stop();
            _expanded = false;
            if (Visible) Hide();
        }

        private void Render()
        {
            AttentionItem it = _list.Latest;
            if (it == null) { HidePill(); return; }
            if (!_hover.Enabled) _hover.Start();
            if (_suppressed) return;   // 全屏期间只保留列表，退出全屏后由悬停计时器恢复
            Rectangle mon;
            float s;
            int cx, bottom;
            _layoutKey = ComputeAnchor(out mon, out s, out cx, out bottom);
            Point pos;
            using (Bitmap bmp = _expanded ? DrawExpanded(it, _list.Count - 1, s) : DrawCollapsed(s))
            {
                ComputeRects(cx, bottom, s, bmp.Size);
                pos = _expanded ? _expRect.Location : _colRect.Location;
                Layered.Apply(Handle, bmp, pos.X, pos.Y);
            }
            SetClickThrough(!_expanded);
            if (!Visible) Show();
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private void ComputeRects(int cx, int bottom, float s, Size cur)
        {
            Size col = CollapsedSize(s);
            _colRect = new Rectangle(cx - col.Width / 2, bottom - LineHeight(s) - (col.Height - LineHeight(s)) / 2, col.Width, col.Height);
            if (_expanded) _expRect = new Rectangle(cx - cur.Width / 2, bottom - cur.Height, cur.Width, cur.Height);
        }

        private void OnHoverTick(object sender, EventArgs e)
        {
            try
            {
                if (_list.Count == 0) { HidePill(); return; }
                if (CheckFullscreen()) return;
                Point p = Cursor.Position;
                DateTime now = DateTime.Now;
                if (_expanded)
                {
                    Rectangle area = Rectangle.Union(_expRect, _colRect);
                    area.Inflate(2, 2);
                    if (area.Contains(p))
                    {
                        DateTime min = now.AddSeconds(LeaveSeconds);
                        if (_collapseAt < min) _collapseAt = min;
                    }
                    else if (now >= _collapseAt) { _expanded = false; Render(); }
                }
                else if (_colRect.Contains(p))
                {
                    _expanded = true;
                    _collapseAt = now.AddSeconds(LeaveSeconds);
                    Render();
                }
            }
            catch (Exception ex) { Log.Error("HoverTick", ex); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            try
            {
                if (e.Button == MouseButtons.Left && ActivateRequested != null) ActivateRequested(this, EventArgs.Empty);
                else if (e.Button == MouseButtons.Right && DismissRequested != null) DismissRequested(this, EventArgs.Empty);
            }
            catch (Exception ex) { Log.Error("PillClick", ex); }
        }
    }
}
