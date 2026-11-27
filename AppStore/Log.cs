using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AppStore
{
    /// <summary>
    /// Appends timestamped lines to AppStore-log.txt next to the exe. Past
    /// 128 KB it becomes AppStore-log.old.txt. Never throws.
    /// </summary>
    public static class Log
    {
        const long MaxBytes = 128 * 1024;
        static readonly object gate = new object();

        public static string FilePath { get { return Path.Combine(Store.AppDir, "AppStore-log.txt"); } }
        static string OldPath { get { return Path.Combine(Store.AppDir, "AppStore-log.old.txt"); } }

        public static void Write(string message)
        {
            if (message == null || message.Length == 0) return;
            lock (gate)
            {
                try
                {
                    FileInfo fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        if (File.Exists(OldPath)) File.Delete(OldPath);
                        File.Move(FilePath, OldPath);
                    }
                    using (StreamWriter w = new StreamWriter(FilePath, true, Encoding.UTF8))
                    {
                        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        foreach (string line in message.Replace("\r\n", "\n").Split('\n'))
                            if (line.Trim().Length > 0) w.WriteLine(stamp + "  " + line.TrimEnd());
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>The last `count` lines, newest last.</summary>
        public static string Tail(int count)
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return "(log is empty)";
                    List<string> lines = new List<string>();
                    using (StreamReader r = new StreamReader(FilePath, Encoding.UTF8))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            lines.Add(line);
                            if (lines.Count > count) lines.RemoveAt(0);
                        }
                    }
                    StringBuilder sb = new StringBuilder();
                    foreach (string l in lines) sb.Append(l).Append("\r\n");
                    return sb.ToString();
                }
                catch (Exception ex) { return "Couldn't read log: " + ex.Message; }
            }
        }
    }
}
