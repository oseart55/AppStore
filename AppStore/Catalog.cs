using System;
using System.Collections.Generic;
using System.IO;

namespace AppStore
{
    /// <summary>One app listed in catalog.json.</summary>
    public class CatalogApp
    {
        public string Id = "", Name = "", Repo = "", Description = "";
        /// <summary>Install folder name, under \Application (or \Program Files).</summary>
        public string Folder = "";
        /// <summary>The exe to start and to make a shortcut to (default: the only/first .exe).</summary>
        public string Exe = "";
        /// <summary>Start menu shortcut name; "" for none.</summary>
        public string Shortcut = "";
        /// <summary>Arguments to run the exe with after install/update, and before uninstall.</summary>
        public string InstallArgs = "", UninstallArgs = "";
        /// <summary>Release asset names to use (wildcards). Empty: see Installer.PickAssets.</summary>
        public List<string> Assets = new List<string>();
        /// <summary>Files never overwritten once they exist (settings the user edits).</summary>
        public List<string> Keep = new List<string>();
        /// <summary>Log files the app writes in its folder (wildcards), for "Send logs".</summary>
        public List<string> Logs = new List<string>();

        // Filled in by the store.
        public ReleaseInfo Latest;
        public string LatestError;
        public InstalledApp Installed;

        public bool UpdateAvailable
        {
            get { return Installed != null && Latest != null && Installer.IsNewer(Latest, Installed); }
        }
    }

    public class ReleaseAsset
    {
        public string Name = "", Url = "", Sha256 = "";
        public int Size;
    }

    public class ReleaseInfo
    {
        public string Tag = "", Notes = "", Published = "";
        public Version Version;
        public List<ReleaseAsset> Assets = new List<ReleaseAsset>();
    }

    public static class Catalog
    {
        /// <summary>Downloads and parses the catalog. Throws IOException.</summary>
        public static List<CatalogApp> Fetch(out string title)
        {
            int status;
            string body = Store.GetText(Store.CatalogUrl, out status);
            if (status == 404) throw new IOException("No catalog at " + Store.CatalogUrl);
            if (status != 200) throw new IOException("The catalog returned HTTP " + status);
            return Parse(body, out title);
        }

        public static List<CatalogApp> Parse(string json, out string title)
        {
            Dictionary<string, object> root;
            try { root = MiniJson.Parse(json) as Dictionary<string, object>; }
            catch (Exception ex) { throw new IOException("The catalog isn't valid JSON: " + ex.Message); }
            if (root == null) throw new IOException("The catalog isn't a JSON object");

            title = MiniJson.Str(root, "name");
            List<CatalogApp> apps = new List<CatalogApp>();
            List<object> list = MiniJson.Arr(root, "apps");
            if (list == null) throw new IOException("The catalog has no \"apps\" list");

            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (object o in list)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                CatalogApp a = new CatalogApp();
                a.Repo = MiniJson.Str(d, "repo").Trim().Trim('/');
                a.Name = MiniJson.Str(d, "name").Trim();
                if (!ValidRepo(a.Repo) || a.Name.Length == 0) continue;

                a.Id = MiniJson.Str(d, "id").Trim().ToLower();
                if (a.Id.Length == 0) a.Id = a.Repo.ToLower();
                if (seen.ContainsKey(a.Id)) continue;
                seen[a.Id] = true;

                a.Description = MiniJson.Str(d, "description").Trim();
                a.Folder = SafeName(MiniJson.Str(d, "folder"));
                if (a.Folder.Length == 0) a.Folder = SafeName(a.Name.Replace(" ", ""));
                if (a.Folder.Length == 0) continue;
                a.Exe = SafeName(MiniJson.Str(d, "exe"));

                object sc;
                if (d.TryGetValue("shortcut", out sc) && sc is bool) a.Shortcut = (bool)sc ? a.Name : "";
                else if (sc is string) a.Shortcut = SafeName((string)sc);
                else a.Shortcut = SafeName(a.Name);

                a.InstallArgs = MiniJson.Str(d, "install").Trim();
                a.UninstallArgs = MiniJson.Str(d, "uninstall").Trim();
                a.Assets = Strings(d, "assets");
                a.Keep = Strings(d, "keep");
                a.Logs = Strings(d, "logs");
                apps.Add(a);
            }
            return apps;
        }

        /// <summary>The newest non-draft, non-prerelease release; null if the repo has none.</summary>
        public static ReleaseInfo LatestRelease(string repo)
        {
            int status;
            string body = Store.GetText("https://api.github.com/repos/" + repo + "/releases/latest", out status);
            if (status == 404) return null;
            if (status != 200) throw new IOException("GitHub returned HTTP " + status);

            Dictionary<string, object> root = MiniJson.Parse(body) as Dictionary<string, object>;
            if (root == null) throw new IOException("Unexpected reply from GitHub");

            ReleaseInfo r = new ReleaseInfo();
            r.Tag = MiniJson.Str(root, "tag_name");
            r.Version = ParseVersion(r.Tag);
            r.Notes = MiniJson.Str(root, "body");
            r.Published = MiniJson.Str(root, "published_at");
            List<object> assets = MiniJson.Arr(root, "assets");
            if (assets != null)
                foreach (object o in assets)
                {
                    Dictionary<string, object> a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    ReleaseAsset x = new ReleaseAsset();
                    x.Name = MiniJson.Str(a, "name");
                    x.Url = MiniJson.Str(a, "browser_download_url");
                    object sz;
                    if (a.TryGetValue("size", out sz) && sz is double) x.Size = (int)(double)sz;
                    string digest = MiniJson.Str(a, "digest");
                    if (digest.StartsWith("sha256:")) x.Sha256 = digest.Substring(7).ToLower();
                    if (x.Name.Length > 0 && x.Url.StartsWith("https://") && ZipReader.IsSafeName(x.Name) && x.Name.IndexOf('\\') < 0)
                        r.Assets.Add(x);
                }
            return r;
        }

        // "v1.2" / "1.2.3" / "V1.2.3.4" -> Version; null if it isn't one.
        public static Version ParseVersion(string tag)
        {
            if (tag == null) return null;
            string s = tag.Trim();
            if (s.StartsWith("v") || s.StartsWith("V")) s = s.Substring(1);
            string[] parts = s.Split('.');
            if (parts.Length < 1 || parts.Length > 4) return null;
            int[] n = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                try { n[i] = int.Parse(parts[i]); }
                catch (Exception) { return null; }
                if (n[i] < 0) return null;
            }
            return new Version(n[0], n[1], n[2], n[3]);
        }

        static bool ValidRepo(string repo)
        {
            int slash = repo.IndexOf('/');
            if (slash <= 0 || slash == repo.Length - 1 || repo.IndexOf('/', slash + 1) >= 0) return false;
            foreach (char c in repo)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '/')) return false;
            return true;
        }

        // A single file or folder name: no path separators, no "..", no wildcards.
        static string SafeName(string s)
        {
            s = (s ?? "").Trim();
            if (s.IndexOfAny(new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) return "";
            if (s == "." || s == "..") return "";
            return s;
        }

        static List<string> Strings(Dictionary<string, object> d, string key)
        {
            List<string> result = new List<string>();
            List<object> arr = MiniJson.Arr(d, key);
            if (arr != null)
                foreach (object o in arr)
                {
                    string s = o as string;
                    if (s != null && s.Trim().Length > 0) result.Add(s.Trim().Replace('/', '\\'));
                }
            return result;
        }
    }
}
