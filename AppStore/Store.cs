using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CeHttps;

namespace AppStore
{
    /// <summary>
    /// Settings and GitHub access. Settings come from store.cfg next to the exe:
    ///
    ///     catalog=owner/repo          catalog.json on that repo's default branch
    ///     catalog=https://.../x.json  or any URL
    ///     token=...                   optional GitHub token (private repos, higher
    ///                                 rate limit). Never put one in a release.
    /// </summary>
    public static class Store
    {
        public const string DefaultCatalog = "oseart55/MC55A-Apps";
        const string ConfigName = "store.cfg";

        public static string AppDir
        {
            get { return Path.GetDirectoryName(OwnExe); }
        }

        public static string OwnExe
        {
            get { return Assembly.GetExecutingAssembly().GetName().CodeBase; }
        }

        public static Version Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static string VersionText
        {
            get { Version v = Version; return "v" + v.Major + "." + v.Minor + "." + v.Build; }
        }

        static Dictionary<string, string> config;

        static string Setting(string key)
        {
            if (config == null)
            {
                Dictionary<string, string> c = new Dictionary<string, string>();
                try
                {
                    string path = Path.Combine(AppDir, ConfigName);
                    if (File.Exists(path))
                        using (StreamReader r = new StreamReader(path))
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
                }
                catch (Exception) { }
                config = c;
            }
            string v;
            return config.TryGetValue(key, out v) && v.Length > 0 ? v : null;
        }

        public static void ReloadSettings() { config = null; }

        /// <summary>What the catalog setting says (owner/repo or a URL).</summary>
        public static string CatalogSource
        {
            get { return Setting("catalog") ?? DefaultCatalog; }
        }

        public static string CatalogUrl
        {
            get
            {
                string c = CatalogSource;
                if (c.StartsWith("https://") || c.StartsWith("http://")) return c;
                return "https://raw.githubusercontent.com/" + c.Trim('/') + "/HEAD/catalog.json";
            }
        }

        static string Token { get { return Setting("token"); } }

        // ------------------------------------------------------------- HTTP

        /// <summary>GET with GitHub headers. Throws IOException with a readable message.</summary>
        public static HttpsResponse Get(string url, int maxBytes, int timeoutMs)
        {
            HttpsRequest req = new HttpsRequest("GET", url);
            req.MaxResponseBytes = maxBytes;
            req.TimeoutMs = timeoutMs;
            if (url.StartsWith("https://api.github.com/")) req.AddHeader("Accept", "application/vnd.github+json");
            // Only send the token to GitHub itself, never to the CDN a download redirects to.
            if (Token != null && (url.StartsWith("https://api.github.com/") || url.StartsWith("https://raw.githubusercontent.com/")))
                req.AddHeader("Authorization", "token " + Token);

            HttpsResponse r = Send(req);
            CheckRateLimit(r);
            return r;
        }

        /// <summary>Sends a request; throws IOException with a readable message for network problems.</summary>
        public static HttpsResponse Send(HttpsRequest req)
        {
            HttpsResponse r;
            try { r = Https.Send(req); }
            catch (HttpsException ex)
            {
                if (ex.IsClockProblem) throw new IOException("The clock looks wrong, so secure connections fail. Set the date and time.");
                if (ex.IsDnsOrNetwork) throw new IOException("No network connection (" + ex.Message + ")");
                if (ex.IsTimeout) throw new IOException("The connection timed out");
                throw new IOException(ex.Message);
            }
            return r;
        }

        static void CheckRateLimit(HttpsResponse r)
        {
            if ((r.Status == 403 || r.Status == 429) && r.Header("x-ratelimit-remaining") == "0")
            {
                string when = "";
                try
                {
                    long reset = long.Parse(r.Header("x-ratelimit-reset"));
                    DateTime t = new DateTime(1970, 1, 1, 0, 0, 0).AddSeconds(reset).ToLocalTime();
                    when = " Try again after " + t.ToString("HH:mm") + ".";
                }
                catch (Exception) { }
                throw new IOException("GitHub's limit of 60 requests an hour was reached." + when);
            }
        }

        public static string GetText(string url, out int status)
        {
            HttpsResponse r = Get(url, 2 * 1024 * 1024, 30000);
            status = r.Status;
            return r.Text;
        }
    }
}
