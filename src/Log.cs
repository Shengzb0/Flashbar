using System;
using System.IO;
using System.Text;

namespace FlashBar
{
    // 简单日志：%LOCALAPPDATA%\FlashBar\flashbar.log，超过 512KB 轮转一次
    internal static class Log
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object Gate = new object();
        private static string _path;

        public static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashBar");
                    _path = Path.Combine(dir, "flashbar.log");
                }
                return _path;
            }
        }

        public static void Info(string msg)
        {
            Write("INFO", msg);
        }

        public static void Error(string where, Exception ex)
        {
            Write("ERROR", where + ": " + (ex == null ? "(null)" : ex.ToString()));
        }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    string p = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > MaxBytes) Rotate(p);
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + msg + Environment.NewLine;
                    File.AppendAllText(p, line, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败不影响运行
            }
        }

        // 轮转失败（.1 只读/被占用）时退而清空当前日志，保证之后还能继续写
        private static void Rotate(string p)
        {
            try
            {
                string old = p + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(p, old);
            }
            catch
            {
                try { File.WriteAllText(p, "", Encoding.UTF8); }
                catch { }
            }
        }
    }
}
