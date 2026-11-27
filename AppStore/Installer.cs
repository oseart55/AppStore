using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AppStore
{
    /// <summary>
    /// An installed app, read from the appstore.inf manifest the store writes
    /// into each app's folder. Keeping it there (not in the registry) means the
    /// store still knows the app after a clean boot, since \Application survives.
    /// </summary>
    public class InstalledApp
    {
        public string Id = "", Name = "", Tag = "", Folder = "", Exe = "", Shortcut = "", UninstallArgs = "";
        public List<string> Files = new List<string>();
        public List<string> Keep = new List<string>();
        public string ExePath { get { return Exe.Length == 0 ? "" : Path.Combine(Folder, Exe); } }
        public string ManifestPath { get { return Path.Combine(Folder, Installer.ManifestName); } }
    }

    public class InstallResult
    {
        public string Message = "";
        /// <summary>The store updated itself: it must exit now so the update can finish.</summary>
        public bool StoreMustExit;
    }

    public delegate void Progress(string message);

    public static class Installer
    {
        public const string ManifestName = "appstore.inf";
        const string PlanName = "apply.plan";
        const int MaxAssetBytes = 16 * 1024 * 1024;
        static readonly string[] Roots = { @"\Application", @"\Program Files" };
        const string StartMenu = @"\Windows\Start Menu\Programs";

        /// <summary>Where new apps go: \Application survives a clean boot on Motorola devices.</summary>
        public static string InstallRoot
        {
            get { return Directory.Exists(Roots[0]) ? Roots[0] : Roots[1]; }
        }

        static string StagingRoot { get { return Path.Combine(Store.AppDir, "staging"); } }

        // ------------------------------------------------------------- what's installed

        public static List<InstalledApp> FindInstalled()
        {
            List<InstalledApp> found = new List<InstalledApp>();
            foreach (string root in Roots)
            {
                string[] dirs;
                try { if (!Directory.Exists(root)) continue; dirs = Directory.GetDirectories(root); }
                catch (Exception) { continue; }
                foreach (string d in dirs)
                {
                    InstalledApp a = ReadManifest(Path.Combine(d, ManifestName));
                    if (a != null) found.Add(a);
                }
            }
            return found;
        }

        static InstalledApp ReadManifest(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                InstalledApp a = new InstalledApp();
                a.Folder = Path.GetDirectoryName(path);
                using (StreamReader r = new StreamReader(path, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                        if (k == "id") a.Id = v;
                        else if (k == "name") a.Name = v;
                        else if (k == "tag") a.Tag = v;
                        else if (k == "exe") a.Exe = v;
                        else if (k == "shortcut") a.Shortcut = v;
                        else if (k == "uninstall") a.UninstallArgs = v;
                        else if (k == "files") a.Files = SplitList(v);
                        else if (k == "keep") a.Keep = SplitList(v);
                    }
                }
                if (a.Id.Length == 0) return null;
                if (a.Name.Length == 0) a.Name = a.Id;
                return a;
            }
            catch (Exception) { return null; }
        }

        static string ManifestText(InstalledApp a)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# Written by App Store. Lists the files it installed, so they can be updated and removed.\r\n");
            sb.Append("id=").Append(a.Id).Append("\r\n");
            sb.Append("name=").Append(a.Name).Append("\r\n");
            sb.Append("tag=").Append(a.Tag).Append("\r\n");
            sb.Append("exe=").Append(a.Exe).Append("\r\n");
            sb.Append("shortcut=").Append(a.Shortcut).Append("\r\n");
            sb.Append("uninstall=").Append(a.UninstallArgs).Append("\r\n");
            sb.Append("files=").Append(string.Join("|", a.Files.ToArray())).Append("\r\n");
            sb.Append("keep=").Append(string.Join("|", a.Keep.ToArray())).Append("\r\n");
            sb.Append("installed=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
            return sb.ToString();
        }

        static List<string> SplitList(string v)
        {
            List<string> list = new List<string>();
            foreach (string s in v.Split('|'))
                if (s.Trim().Length > 0 && ZipReader.IsSafeName(s.Trim())) list.Add(s.Trim());
            return list;
        }

        static void WriteText(string path, string text)
        {
            string tmp = path + ".tmp";
            using (StreamWriter w = new StreamWriter(tmp, false, Encoding.UTF8)) w.Write(text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ------------------------------------------------------------- versions

        /// <summary>What's installed, for display: the exe's own version if it has one, else the tag.</summary>
        public static string InstalledVersionText(InstalledApp a)
        {
            Version v = ExeVersion(a.ExePath);
            if (v != null && v.Major + v.Minor + v.Build + v.Revision > 0)
                return "v" + v.Major + "." + v.Minor + "." + v.Build;
            return a.Tag.Length > 0 ? a.Tag : "?";
        }

        public static bool IsNewer(ReleaseInfo r, InstalledApp a)
        {
            if (r == null || r.Tag.Length == 0) return false;
            if (r.Version != null)
            {
                // An app that updates itself (like Book Scout) may be newer than
                // what the store installed; its exe version tells the truth.
                Version exe = ExeVersion(a.ExePath);
                if (exe != null && exe.CompareTo(r.Version) >= 0) return false;
                Version tag = Catalog.ParseVersion(a.Tag);
                if (tag != null) return r.Version.CompareTo(tag) > 0;
            }
            return string.Compare(r.Tag, a.Tag, StringComparison.OrdinalIgnoreCase) != 0;
        }

        static readonly Dictionary<string, Version> versionCache = new Dictionary<string, Version>();

        /// <summary>
        /// The file version from an exe's version resource, or null. Reads the PE
        /// resource section directly (no version.dll on every device).
        /// </summary>
        public static Version ExeVersion(string path)
        {
            try
            {
                if (path.Length == 0 || !File.Exists(path)) return null;
                FileInfo fi = new FileInfo(path);
                string key = path.ToLower() + "|" + fi.Length + "|" + fi.LastWriteTime.Ticks;
                Version cached;
                if (versionCache.TryGetValue(key, out cached)) return cached;

                Version v = null;
                byte[] data = Files.Read(path, 32 * 1024 * 1024);
                int pe = I32(data, 0x3C);
                if (data.Length > 0x40 && data[0] == 'M' && data[1] == 'Z' && pe > 0 && pe + 24 < data.Length &&
                    I32(data, pe) == 0x00004550)
                {
                    int sections = data[pe + 6] | (data[pe + 7] << 8);
                    int optSize = data[pe + 20] | (data[pe + 21] << 8);
                    int table = pe + 24 + optSize;
                    for (int s = 0; s < sections && table + s * 40 + 40 <= data.Length; s++)
                    {
                        int h = table + s * 40;
                        if (Encoding.ASCII.GetString(data, h, 5) != ".rsrc") continue;
                        int size = I32(data, h + 16), raw = I32(data, h + 20);
                        int stop = Math.Min(data.Length - 16, raw + size - 16);
                        for (int i = raw; i < stop; i += 4)
                            if (I32(data, i) == unchecked((int)0xFEEF04BD))
                            {
                                int ms = I32(data, i + 8), ls = I32(data, i + 12);
                                v = new Version((ms >> 16) & 0xFFFF, ms & 0xFFFF, (ls >> 16) & 0xFFFF, ls & 0xFFFF);
                                break;
                            }
                    }
                }
                versionCache[key] = v;
                return v;
            }
            catch (Exception) { return null; }
        }

        static int I32(byte[] d, int p)
        {
            if (p < 0 || p + 4 > d.Length) return 0;
            return d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);
        }

        // ------------------------------------------------------------- install / update

        /// <summary>The release files to install (see the catalog's "assets").</summary>
        public static List<ReleaseAsset> PickAssets(CatalogApp app, ReleaseInfo r)
        {
            List<ReleaseAsset> picked = new List<ReleaseAsset>();
            if (app.Assets.Count > 0)
            {
                foreach (ReleaseAsset a in r.Assets)
                    foreach (string pattern in app.Assets)
                        if (Wild(a.Name, pattern)) { picked.Add(a); break; }
                return picked;
            }
            List<ReleaseAsset> zips = new List<ReleaseAsset>();
            foreach (ReleaseAsset a in r.Assets) if (IsZip(a.Name)) zips.Add(a);
            if (zips.Count == 1) return zips;
            if (zips.Count > 1)
                throw new IOException("The release has several .zip files. Add an \"assets\" list to this app in the catalog.");
            return new List<ReleaseAsset>(r.Assets);
        }

        public static InstallResult Install(CatalogApp app, Progress progress)
        {
            ReleaseInfo r = app.Latest;
            if (r == null) throw new IOException(app.Name + " has no release on GitHub yet");
            List<ReleaseAsset> assets = PickAssets(app, r);
            if (assets.Count == 0) throw new IOException("The release " + r.Tag + " has no files to install");

            string stage = Path.Combine(StagingRoot, app.Id.Replace('/', '_'));
            Files.DeleteTree(stage);
            Directory.CreateDirectory(stage);
            try
            {
                // 1. Download (and unzip) everything into the staging folder.
                int n = 0;
                foreach (ReleaseAsset a in assets)
                {
                    n++;
                    Say(progress, "Downloading " + a.Name + (assets.Count > 1 ? " (" + n + "/" + assets.Count + ")" : "") +
                        (a.Size > 0 ? ", " + Kb(a.Size) : "") + "...");
                    byte[] data = Download(a);
                    if (IsZip(a.Name))
                    {
                        Say(progress, "Unpacking " + a.Name + "...");
                        Unzip(data, stage);
                    }
                    else Files.Write(Path.Combine(stage, a.Name), data);
                }

                List<string> files = Files.List(stage);
                files.Remove(PlanName);
                files.Remove(ManifestName);
                if (files.Count == 0) throw new IOException("The release contained no files");

                // 2. Work out where it goes and what the manifest will say.
                InstalledApp old = app.Installed;
                string target = old != null ? old.Folder : Path.Combine(InstallRoot, app.Folder);
                InstalledApp m = new InstalledApp();
                m.Id = app.Id;
                m.Name = app.Name;
                m.Tag = r.Tag;
                m.Folder = target;
                m.Exe = app.Exe.Length > 0 ? app.Exe : FirstExe(files);
                if (m.Exe.Length > 0 && !Contains(files, m.Exe) && !(old != null && File.Exists(Path.Combine(target, m.Exe))))
                    throw new IOException("The release doesn't contain " + m.Exe);
                m.Shortcut = app.Shortcut.Length > 0 && m.Exe.Length > 0 ? Path.Combine(StartMenu, app.Shortcut + ".lnk") : "";
                m.UninstallArgs = app.UninstallArgs;
                m.Keep = new List<string>(app.Keep);
                m.Files = files;
                List<string> oldFiles = old != null ? old.Files : new List<string>();

                // 3. The store updating itself can't overwrite its running exe:
                //    hand over to the new exe, which finishes after we exit.
                if (Files.SameFolder(target, Store.AppDir))
                {
                    string plan = "target=" + target + "\r\nold=" + string.Join("|", oldFiles.ToArray()) + "\r\n";
                    WriteText(Path.Combine(stage, PlanName), plan);
                    WriteText(Path.Combine(stage, ManifestName), ManifestText(m));
                    string newExe = Path.Combine(stage, Path.GetFileName(Store.OwnExe));
                    if (!File.Exists(newExe)) throw new IOException("The release doesn't contain " + Path.GetFileName(Store.OwnExe));
                    Log.Write("Self-update to " + r.Tag + ": handing over to " + newExe);
                    Process.Start(newExe, "/apply");
                    InstallResult self = new InstallResult();
                    self.StoreMustExit = true;
                    self.Message = "Updating App Store to " + r.Tag + ". It restarts by itself.";
                    return self;
                }

                // 4. Make sure the app isn't running, then copy it in.
                if (old != null && m.Exe.Length > 0 && Files.IsInUse(Path.Combine(target, m.Exe)))
                    throw new IOException(app.Name + " is running. Close it, then try again.");

                Say(progress, "Installing to " + target + "...");
                CopyInto(stage, target, m, oldFiles);
                WriteText(m.ManifestPath, ManifestText(m));
                if (m.Shortcut.Length > 0) WriteShortcut(m.Shortcut, m.ExePath);
                if (old != null && old.Shortcut.Length > 0 && !Files.SamePath(old.Shortcut, m.Shortcut)) Files.TryDelete(old.Shortcut);

                // 5. The app's own setup step.
                InstallResult result = new InstallResult();
                result.Message = (old == null ? "Installed " : "Updated ") + app.Name + " " + r.Tag + " in " + target + ".";
                if (app.InstallArgs.Length > 0 && m.Exe.Length > 0)
                {
                    Say(progress, "Running " + m.Exe + " " + app.InstallArgs + "...");
                    result.Message += "\r\n" + RunHook(m.ExePath, app.InstallArgs);
                }
                if (m.Shortcut.Length > 0) result.Message += "\r\nShortcut: Start > Programs > " + app.Shortcut;
                Log.Write(result.Message);
                return result;
            }
            finally
            {
                if (!File.Exists(Path.Combine(stage, PlanName))) Files.DeleteTree(stage);
            }
        }

        static byte[] Download(ReleaseAsset a)
        {
            if (a.Size > MaxAssetBytes) throw new IOException(a.Name + " is too big (" + Kb(a.Size) + ")");
            CeHttps.HttpsResponse resp = Store.Get(a.Url, MaxAssetBytes, 300000);
            if (resp.Status != 200) throw new IOException(a.Name + ": HTTP " + resp.Status);
            byte[] data = resp.Body;
            if (a.Size > 0 && data.Length != a.Size)
                throw new IOException(a.Name + ": expected " + a.Size + " bytes, got " + data.Length);
            if (a.Sha256.Length > 0 && CeHttps.Https.Sha256Hex(data) != a.Sha256)
                throw new IOException(a.Name + ": checksum mismatch - the download is damaged");
            return data;
        }

        static void Unzip(byte[] data, string stage)
        {
            ZipReader z = new ZipReader(data);
            List<ZipEntry> files = new List<ZipEntry>();
            foreach (ZipEntry e in z.Entries)
            {
                if (!ZipReader.IsSafeName(e.Name)) throw new IOException("The zip contains an unsafe path: " + e.Name);
                if (!e.IsDirectory) files.Add(e);
            }

            // A zip of "MyApp\..." unpacks without the MyApp folder.
            string strip = CommonTopFolder(files);
            foreach (ZipEntry e in files)
            {
                string rel = strip.Length > 0 ? e.Name.Substring(strip.Length) : e.Name;
                Files.Write(Path.Combine(stage, rel), z.Extract(e));
            }
        }

        static string CommonTopFolder(List<ZipEntry> files)
        {
            if (files.Count == 0) return "";
            string top = null;
            foreach (ZipEntry e in files)
            {
                int slash = e.Name.IndexOf('\\');
                if (slash <= 0) return "";
                string t = e.Name.Substring(0, slash + 1);
                if (top == null) top = t;
                else if (string.Compare(top, t, StringComparison.OrdinalIgnoreCase) != 0) return "";
            }
            return top;
        }

        /// <summary>
        /// Copies the staged files over the app folder, leaves "keep" files that
        /// already exist alone, and removes files the old version had that the new
        /// one doesn't. Files the user made (not in either list) are never touched.
        /// </summary>
        static void CopyInto(string stage, string target, InstalledApp m, List<string> oldFiles)
        {
            Directory.CreateDirectory(target);
            foreach (string rel in m.Files)
            {
                string dst = Path.Combine(target, rel);
                if (File.Exists(dst) && IsKept(rel, m.Keep)) continue;
                string dir = Path.GetDirectoryName(dst);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Files.CopyWithRetry(Path.Combine(stage, rel), dst);
            }
            foreach (string rel in oldFiles)
            {
                if (Contains(m.Files, rel) || IsKept(rel, m.Keep)) continue;
                Files.TryDelete(Path.Combine(target, rel));
                Files.RemoveEmptyDirs(Path.GetDirectoryName(Path.Combine(target, rel)), target);
            }
        }

        /// <summary>
        /// "AppStore.exe /apply", run from the staging folder by the old store:
        /// waits for it to exit, copies the new version over it, and starts it.
        /// </summary>
        public static void ApplySelfUpdate()
        {
            string stage = Store.AppDir;
            string target = null;
            List<string> oldFiles = new List<string>();
            using (StreamReader r = new StreamReader(Path.Combine(stage, PlanName)))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.StartsWith("target=")) target = line.Substring(7);
                    else if (line.StartsWith("old=")) oldFiles = SplitList(line.Substring(4));
                }
            }
            if (target == null) throw new IOException("bad update plan");
            InstalledApp m = ReadManifest(Path.Combine(stage, ManifestName));
            if (m == null) throw new IOException("bad update manifest");
            m.Folder = target;

            string targetExe = Path.Combine(target, Path.GetFileName(Store.OwnExe));
            for (int i = 0; i < 60 && Files.IsInUse(targetExe); i++) Thread.Sleep(500);

            CopyInto(stage, target, m, oldFiles);
            WriteText(m.ManifestPath, ManifestText(m));
            if (m.Shortcut.Length > 0) WriteShortcut(m.Shortcut, m.ExePath);
            Log.Write("Self-update applied: " + m.Tag);
            Process.Start(targetExe, "");
        }

        /// <summary>Removes the staging folder left by a finished self-update.</summary>
        public static void CleanStaging()
        {
            try
            {
                if (Directory.Exists(StagingRoot)) Files.DeleteTree(StagingRoot);
            }
            catch (Exception) { }   // the updater may still be exiting; next start cleans it
        }

        // ------------------------------------------------------------- uninstall

        public static string Uninstall(InstalledApp a, Progress progress)
        {
            if (Files.SameFolder(a.Folder, Store.AppDir))
                throw new IOException("App Store can't uninstall itself. Delete " + a.Folder + " in File Explorer.");

            string message = "Uninstalled " + a.Name + ".";
            if (a.UninstallArgs.Length > 0 && File.Exists(a.ExePath))
            {
                Say(progress, "Running " + a.Exe + " " + a.UninstallArgs + "...");
                message += "\r\n" + RunHook(a.ExePath, a.UninstallArgs);
            }
            if (a.Exe.Length > 0 && Files.IsInUse(a.ExePath))
                throw new IOException(a.Name + " is running. Close it, then try again.");

            Say(progress, "Removing " + a.Name + "...");
            int failed = 0;
            foreach (string rel in a.Files)
            {
                string path = Path.Combine(a.Folder, rel);
                if (File.Exists(path) && !Files.TryDelete(path)) failed++;
                Files.RemoveEmptyDirs(Path.GetDirectoryName(path), a.Folder);
            }
            if (a.Shortcut.Length > 0) Files.TryDelete(a.Shortcut);
            Files.TryDelete(a.ManifestPath);

            int left = Files.List(a.Folder).Count;
            if (left == 0) { try { Directory.Delete(a.Folder); } catch (Exception) { } }
            else message += "\r\nKept " + left + (left == 1 ? " file" : " files") + " you made (settings, data) in " + a.Folder + ".";
            if (failed > 0) message += "\r\n" + failed + " file(s) couldn't be deleted - restart and try again.";
            Log.Write(message);
            return message;
        }

        // ------------------------------------------------------------- shortcuts, hooks

        /// <summary>Recreates missing Start menu shortcuts (a clean boot wipes \Windows).</summary>
        public static int RepairShortcuts(List<InstalledApp> apps)
        {
            int fixedCount = 0;
            foreach (InstalledApp a in apps)
            {
                if (a.Shortcut.Length == 0 || File.Exists(a.Shortcut) || !File.Exists(a.ExePath)) continue;
                try { WriteShortcut(a.Shortcut, a.ExePath); fixedCount++; }
                catch (Exception) { }
            }
            if (fixedCount > 0) Log.Write("Recreated " + fixedCount + " Start menu shortcut(s)");
            return fixedCount;
        }

        // Windows CE shortcut: "<length>#<command line>".
        static void WriteShortcut(string lnk, string target)
        {
            string dir = Path.GetDirectoryName(lnk);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string cmd = "\"" + target + "\"";
            using (StreamWriter w = new StreamWriter(lnk, false, Encoding.ASCII))
                w.Write(cmd.Length + "#" + cmd);
        }

        static string RunHook(string exe, string args)
        {
            try
            {
                Log.Write("Running " + exe + " " + args);
                using (Process p = Process.Start(exe, args))
                {
                    // Generous: a hook may show a message the user has to close.
                    if (!p.WaitForExit(180000)) return Path.GetFileName(exe) + " " + args + " is still running.";
                    return p.ExitCode == 0 ? "Setup step finished." : "Setup step ended with code " + p.ExitCode + ".";
                }
            }
            catch (Exception ex) { return "Setup step failed: " + ex.Message; }
        }

        // ------------------------------------------------------------- helpers

        static void Say(Progress p, string message) { if (p != null) p(message); }

        public static string Kb(int bytes)
        {
            return bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("0.0") + " MB" : (bytes + 1023) / 1024 + " KB";
        }

        static bool IsZip(string name) { return name.ToLower().EndsWith(".zip"); }

        static string FirstExe(List<string> files)
        {
            foreach (string f in files) if (f.IndexOf('\\') < 0 && f.ToLower().EndsWith(".exe")) return f;
            return "";
        }

        static bool Contains(List<string> list, string item)
        {
            foreach (string s in list) if (string.Compare(s, item, StringComparison.OrdinalIgnoreCase) == 0) return true;
            return false;
        }

        static bool IsKept(string rel, List<string> keep)
        {
            foreach (string pattern in keep)
                if (Wild(rel, pattern) || Wild(Path.GetFileName(rel), pattern)) return true;
            return false;
        }

        /// <summary>Case-insensitive match with * and ?.</summary>
        public static bool Wild(string text, string pattern)
        {
            return WildAt(text.ToLower(), 0, pattern.ToLower(), 0);
        }

        static bool WildAt(string t, int ti, string p, int pi)
        {
            while (pi < p.Length)
            {
                char c = p[pi];
                if (c == '*')
                {
                    for (int k = ti; k <= t.Length; k++)
                        if (WildAt(t, k, p, pi + 1)) return true;
                    return false;
                }
                if (ti >= t.Length || (c != '?' && c != t[ti])) return false;
                ti++;
                pi++;
            }
            return ti == t.Length;
        }
    }

    /// <summary>File helpers the Compact Framework lacks.</summary>
    public static class Files
    {
        public static byte[] Read(string path, int max)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length > max) throw new IOException(path + " is too big");
                byte[] data = new byte[(int)fs.Length];
                int got = 0;
                while (got < data.Length)
                {
                    int n = fs.Read(data, got, data.Length - got);
                    if (n <= 0) throw new IOException("Unexpected end of " + path);
                    got += n;
                }
                return data;
            }
        }

        public static void Write(string path, byte[] data)
        {
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                fs.Write(data, 0, data.Length);
        }

        /// <summary>Every file under the folder, as relative paths.</summary>
        public static List<string> List(string root)
        {
            List<string> result = new List<string>();
            if (Directory.Exists(root)) Walk(root, "", result);
            return result;
        }

        static void Walk(string dir, string prefix, List<string> result)
        {
            foreach (string f in Directory.GetFiles(dir)) result.Add(prefix + Path.GetFileName(f));
            foreach (string d in Directory.GetDirectories(dir)) Walk(d, prefix + Path.GetFileName(d) + "\\", result);
        }

        public static void DeleteTree(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        public static bool TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>Removes empty folders from `dir` up to (not including) `stop`.</summary>
        public static void RemoveEmptyDirs(string dir, string stop)
        {
            try
            {
                while (dir != null && dir.Length > stop.Length && !SamePath(dir, stop) && Directory.Exists(dir) &&
                       Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception) { }
        }

        public static void CopyWithRetry(string src, string dst)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(src, dst, true); return; }
                catch (IOException)
                {
                    if (attempt >= 10) throw new IOException("Couldn't replace " + dst + " - is the app running?");
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>True if the file is open elsewhere (e.g. the exe is running).</summary>
        public static bool IsInUse(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static bool SamePath(string a, string b)
        {
            return string.Compare(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == 0;
        }

        public static bool SameFolder(string a, string b) { return SamePath(a, b); }
    }
}
