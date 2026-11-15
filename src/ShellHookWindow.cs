using System;
using System.Windows.Forms;
using N = FlashBar.NativeMethods;

namespace FlashBar
{
    internal delegate void HwndHandler(IntPtr hwnd);

    // 隐藏窗口，接收 SHELLHOOK 通知
    internal sealed class ShellHookWindow : NativeWindow, IDisposable
    {
        private readonly int _shellMsg;
        private bool _registered;

        public event HwndHandler Flash;
        public event HwndHandler Activated;
        public event HwndHandler Destroyed;

        public ShellHookWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "FlashBarHook";
            CreateHandle(cp);
            _shellMsg = N.RegisterWindowMessage("SHELLHOOK");
            _registered = N.RegisterShellHookWindow(Handle);
            if (!_registered)
                Log.Info("RegisterShellHookWindow failed, err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            else
                Log.Info("shell hook registered, msg=0x" + _shellMsg.ToString("X"));
        }

        protected override void WndProc(ref Message m)
        {
            if (_shellMsg != 0 && m.Msg == _shellMsg)
            {
                try { Dispatch(m.WParam.ToInt64(), m.LParam); }
                catch (Exception ex) { Log.Error("ShellHook", ex); }
                return;
            }
            base.WndProc(ref m);
        }

        private void Dispatch(long code, IntPtr h)
        {
            int c = (int)(code & 0xFFFF);
            switch (c)
            {
                case N.HSHELL_FLASH:
                    if (Flash != null) Flash(h);
                    break;
                case N.HSHELL_WINDOWACTIVATED:
                case N.HSHELL_RUDEAPPACTIVATED:
                    if (Activated != null) Activated(h);
                    break;
                case N.HSHELL_WINDOWDESTROYED:
                    if (Destroyed != null) Destroyed(h);
                    break;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_registered && Handle != IntPtr.Zero) N.DeregisterShellHookWindow(Handle);
                _registered = false;
                DestroyHandle();
            }
            catch (Exception ex) { Log.Error("ShellHook.Dispose", ex); }
        }
    }
}
