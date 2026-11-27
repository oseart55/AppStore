using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using CeHttps;

namespace AppStore
{
    /// <summary>One log file waiting to be sent.</summary>
    public class LogFile
    {
        public string AppId = "", Path = "", Name = "", Hash = "";
        public byte[] Data;
    }

    /// <summary>
    /// "Send logs": uploads the log files of installed apps (the catalog's "logs"
    /// patterns, plus App Store's own log) to a private GitHub repo, so they don't
    /// have to be copied off the device by hand. Settings come from logs.cfg next
    /// to the exe:
    ///
    ///     repo=owner/repo      a PRIVATE repo: logs contain device details
    ///     token=github_pat_... a fine-grained token for that repo only,
    ///                          Contents: read and write
    ///     device=name          optional; the folder name in the repo
    ///                          (default: the device name)
    ///
    /// Files are stored as {device}/{yyyy-MM-dd}/{HHmmss}-{app}-{file}. A file is
    /// only sent again once it has changed (logs.sent remembers what was sent).
    /// </summary>
    public static class LogUpload
    {
        public const string ConfigName = "logs.cfg";
        const string SentName = "logs.sent";
        /// <summary>Bigger logs send only their last this-many bytes (memory: the upload is
        /// base64 in a string, then UTF-8, then copied natively; CE gives a process 32 MB).</summary>
        const int MaxFileBytes = 1024 * 1024;

        public static string ConfigPath { get { return System.IO.Path.Combine(Store.AppDir, ConfigName); } }
        static string SentPath { get { return System.IO.Path.Combine(Store.AppDir, SentName); } }

        public static string SetupHelp
        {
            get
            {
                return "To send logs, make " + ConfigPath + " with these two lines:\r\n\r\n" +
                    "repo=oseart55/MC55A-Logs\r\ntoken=github_pat_...\r\n\r\n" +
                    "Use a PRIVATE repo, and a fine-grained token limited to that repo with " +
                    "Contents: read and write. Keep the file off GitHub.";
            }
        }

        // ------------------------------------------------------------- settings

        static Dictionary<string, string> ReadConfig()
        {
            Dictionary<string, string> c = new Dictionary<string, string>();
            if (!File.Exists(ConfigPath)) return c;
            using (StreamReader r = new StreamReader(ConfigPath))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) c[line.Substring(0, eq).Trim().ToLower()] = line.Substring(eq + 1).Trim();
                }
            }
            return c;
        }

        static string Get(Dictionary<string, string> c, string key)
        {
            string v;
            return c.TryGetValue(key, out v) && v.Length > 0 ? v : null;
        }

        /// <summary>The repo logs go to, or null if logs.cfg isn't set up.</summary>
        public static string Repo
        {
            get
            {
                try
                {
                    Dictionary<string, string> c = ReadConfig();
                    string repo = Get(c, "repo"), token = Get(c, "token");
                    if (repo == null || token == null) return null;
                    repo = repo.Trim().Trim('/');
                    return repo.Split('/').Length == 2 ? repo : null;
                }
                catch (Exception) { return null; }
            }
        }

        public static string DeviceName
        {
            get
            {
                string n = null;
                try { n = Get(ReadConfig(), "device"); }
                catch (Exception) { }
                if (n == null) try { n = Dns.GetHostName(); } catch (Exception) { }
                return PathPart(n == null || n.Length == 0 ? "device" : n);
            }
        }

        // ------------------------------------------------------------- finding logs

        /// <summary>The logs that are new or changed since they were last sent.</summary>
        public static List<LogFile> FindNew(List<CatalogApp> apps)
        {
            Dictionary<string, bool> sent = ReadSent();
            List<LogFile> result = new List<LogFile>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();

            Add(result, seen, sent, "appstore", Log.FilePath);
            foreach (CatalogApp a in apps)
            {
                if (a.Installed == null || a.Logs.Count == 0) continue;
                List<string> files;
                try { files = Files.List(a.Installed.Folder); }
                catch (Exception) { continue; }
                foreach (string rel in files)
                    foreach (string pattern in a.Logs)
                        if (Installer.Wild(rel, pattern) || Installer.Wild(System.IO.Path.GetFileName(rel), pattern))
                        {
                            Add(result, seen, sent, a.Id, System.IO.Path.Combine(a.Installed.Folder, rel));
                            break;
                        }
            }
            return result;
        }

        static void Add(List<LogFile> result, Dictionary<string, bool> seen, Dictionary<string, bool> sent,
                        string appId, string path)
        {
            string key = path.ToLower();
            if (seen.ContainsKey(key)) return;
            seen[key] = true;
            try
            {
                if (!File.Exists(path)) return;
                byte[] data = ReadTail(path, MaxFileBytes);
                if (data.Length == 0) return;
                string hash = Https.Sha256Hex(data);
                if (sent.ContainsKey(hash)) return;
                LogFile f = new LogFile();
                f.AppId = appId;
                f.Path = path;
                f.Name = System.IO.Path.GetFileName(path);
                f.Data = data;
                f.Hash = hash;
                result.Add(f);
            }
            catch (Exception ex) { Log.Write("Couldn't read " + path + ": " + ex.Message); }
        }

        static byte[] ReadTail(string path, int max)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length;
                byte[] note = new byte[0];
                if (len > max)
                {
                    note = Encoding.UTF8.GetBytes("[App Store: only the last " + (max / 1024) + " KB of " + (len / 1024) + " KB]\r\n");
                    fs.Seek(len - max, SeekOrigin.Begin);
                    len = max;
                }
                byte[] data = new byte[note.Length + (int)len];
                Array.Copy(note, data, note.Length);
                int got = 0;
                while (got < len)
                {
                    int n = fs.Read(data, note.Length + got, (int)len - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < len) { byte[] t = new byte[note.Length + got]; Array.Copy(data, t, t.Length); data = t; }
                return data;
            }
        }

        static Dictionary<string, bool> ReadSent()
        {
            Dictionary<string, bool> d = new Dictionary<string, bool>();
            try
            {
                if (File.Exists(SentPath))
                    using (StreamReader r = new StreamReader(SentPath))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            int sp = line.IndexOf(' ');
                            string h = (sp > 0 ? line.Substring(0, sp) : line).Trim();
                            if (h.Length > 0) d[h] = true;
                        }
                    }
            }
            catch (Exception) { }
            return d;
        }

        static void MarkSent(LogFile f, string repoPath)
        {
            try
            {
                using (StreamWriter w = new StreamWriter(SentPath, true, Encoding.UTF8))
                    w.WriteLine(f.Hash + " " + repoPath);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------- sending

        /// <summary>Sends the files; returns a summary. Throws IOException for setup or network problems.</summary>
        public static string Send(List<LogFile> files, Progress progress)
        {
            Dictionary<string, string> c = ReadConfig();
            string repo = Repo, token = Get(c, "token");
            if (repo == null) throw new IOException(SetupHelp);

            string device = DeviceName;
            DateTime now = DateTime.Now;
            string folder = device + "/" + now.ToString("yyyy-MM-dd") + "/";
            string stamp = now.ToString("HHmmss");
            int done = 0;
            StringBuilder report = new StringBuilder();

            foreach (LogFile f in files)
            {
                string repoPath = folder + stamp + "-" + PathPart(f.AppId) + "-" + PathPart(f.Name);
                if (progress != null) progress("Sending " + f.Name + " (" + Installer.Kb(f.Data.Length) + ")...");

                string json = "{\"message\":" + Json("Log from " + device + ": " + f.Name) +
                              ",\"content\":\"" + Convert.ToBase64String(f.Data) + "\"}";
                HttpsRequest req = new HttpsRequest("PUT", "https://api.github.com/repos/" + repo + "/contents/" + repoPath);
                req.TimeoutMs = 120000;
                req.MaxResponseBytes = 256 * 1024;
                req.AddHeader("Accept", "application/vnd.github+json");
                req.AddHeader("Authorization", "Bearer " + token);
                req.SetBody(json, "application/json");

                HttpsResponse r = Store.Send(req);
                if (r.Status == 201 || r.Status == 200)
                {
                    MarkSent(f, repoPath);
                    done++;
                    report.Append("sent " + f.Name + "\r\n");
                    Log.Write("Sent " + f.Path + " to " + repo + "/" + repoPath);
                    continue;
                }
                string why;
                if (r.Status == 401) why = "GitHub didn't accept the token in " + ConfigName + " (wrong or expired).";
                else if (r.Status == 403 || r.Status == 404)
                    why = "The token can't write to " + repo + ". Check the repo name, and that the token is for that repo with Contents: read and write.";
                else why = "GitHub answered HTTP " + r.Status + ".";
                Log.Write("Sending " + f.Path + " failed: HTTP " + r.Status);
                throw new IOException((done > 0 ? report.ToString() + "\r\n" : "") + "Couldn't send " + f.Name + ": " + why);
            }
            return "Sent " + done + " log" + (done == 1 ? "" : "s") + " to " + repo + "/" + folder + "\r\n\r\n" + report.ToString();
        }

        /// <summary>Letters, digits, '-', '_' and '.' only, for repo paths.</summary>
        static string PathPart(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char ch in s)
                b.Append(char.IsLetterOrDigit(ch) && ch < 128 || ch == '-' || ch == '_' || ch == '.' ? ch : '_');
            string r = b.ToString().Trim('.');
            return r.Length == 0 ? "_" : r;
        }

        static string Json(string s)
        {
            StringBuilder b = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') b.Append('\\').Append(ch);
                else if (ch < ' ') b.Append("\\u").Append(((int)ch).ToString("x4"));
                else b.Append(ch);
            }
            return b.Append('"').ToString();
        }
    }
}
