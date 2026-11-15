using System;
using System.Threading;
using System.Windows.Forms;

namespace FlashBar
{
    internal static class Program
    {
        private const string MutexName = @"Local\FlashBar.SingleInstance.7F3A";

        [STAThread]
        private static int Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew) return 1; // 已有实例在运行

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Error("ThreadException", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Error("UnhandledException(terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception);
                };

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    bool demo = args.Length > 0 && string.Equals(args[0], "--demo", StringComparison.OrdinalIgnoreCase);
                    Application.Run(new AppController(demo));
                }
                catch (Exception ex)
                {
                    Log.Error("Main", ex);
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
