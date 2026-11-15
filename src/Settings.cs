using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FlashBar
{
    // 设置：%LOCALAPPDATA%\FlashBar\settings.ini（key=value），和日志同目录，删掉这个目录即干净
    internal static class Settings
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string> _values;

        private static string FilePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Log.FilePath), "settings.ini"); }
        }

        public static bool GetBool(string key, bool def)
        {
            string v = Get(key);
            if (v == null) return def;
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static void SetBool(string key, bool value)
        {
            Set(key, value ? "1" : "0");
        }

        private static string Get(string key)
        {
            lock (Gate)
            {
                Load();
                string v;
                return _values.TryGetValue(key, out v) ? v : null;
            }
        }

        private static void Set(string key, string value)
        {
            lock (Gate)
            {
                Load();
                _values[key] = value;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (KeyValuePair<string, string> kv in _values)
                        sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                }
                catch (Exception ex) { Log.Error("Settings.Save", ex); }
            }
        }

        private static void Load()
        {
            if (_values != null) return;
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string s = line.Trim();
                    if (s.Length == 0 || s[0] == '#' || s[0] == ';') continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    _values[s.Substring(0, eq).Trim()] = s.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex) { Log.Error("Settings.Load", ex); }
        }
    }
}
