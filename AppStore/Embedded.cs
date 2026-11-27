using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace AppStore
{
    /// <summary>
    /// The files App Store needs next to its exe (the CeHttps TLS library and
    /// the root certificates) are built into
    /// AppStore.exe as resources, so a release is just the exe.
    ///
    /// The Compact Framework can only load a DLL from a file, so on start
    /// Extract() writes them out beside the exe. It must run before anything
    /// touches StoreForm or Https - Program.Main calls it first.
    ///
    /// To keep flash writes down, a marker file records which exe last did
    /// the extraction; a file is only rewritten when its bytes differ.
    /// </summary>
    static class Embedded
    {
        static readonly string[] Files = { "CeHttpsNative.dll", "cacert.pem" };
        const string Prefix = "AppStore.Embedded.";
        const string MarkerName = "embedded.ver";

        /// <summary>False (after telling the user) if the app can't start.</summary>
        public static bool Extract()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string exe = asm.GetName().CodeBase;
            string dir = Path.GetDirectoryName(exe);
            string marker = Path.Combine(dir, MarkerName);
            string stamp = Stamp(asm, exe);

            if (stamp != null && ReadMarker(marker) == stamp && AllPresent(dir))
                return true;

            string current = "";
            try
            {
                foreach (string name in Files)
                {
                    current = name;
                    WriteOne(asm, dir, name);
                }
                current = MarkerName;
                if (stamp != null) WriteMarker(marker, stamp);
                return true;
            }
            catch (Exception ex)
            {
                // If an older copy of the file is still there the app can
                // probably run with it; only stop when something is missing.
                if (AllPresent(dir)) return true;
                MessageBox.Show("App Store couldn't unpack " + current + ":\r\n" + ex.Message +
                    "\r\n\r\nIf another copy of App Store is running, close it and try again.", "App Store");
                return false;
            }
        }

        // Changes whenever a different exe is installed (version, size or build time).
        static string Stamp(Assembly asm, string exe)
        {
            try
            {
                FileInfo fi = new FileInfo(exe);
                return asm.GetName().Version + "|" + fi.Length + "|" + fi.LastWriteTime.Ticks;
            }
            catch (Exception) { return null; }
        }

        static bool AllPresent(string dir)
        {
            foreach (string name in Files)
                if (!File.Exists(Path.Combine(dir, name))) return false;
            return true;
        }

        static string ReadMarker(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (StreamReader r = new StreamReader(path)) return r.ReadToEnd().Trim();
            }
            catch (Exception) { return null; }
        }

        static void WriteMarker(string path, string stamp)
        {
            try { using (StreamWriter w = new StreamWriter(path, false)) w.Write(stamp); }
            catch (Exception) { }   // only costs a byte comparison next start
        }

        static void WriteOne(Assembly asm, string dir, string name)
        {
            byte[] data = ReadResource(asm, Prefix + name);
            string dst = Path.Combine(dir, name);
            if (SameAsFile(dst, data)) return;

            string tmp = dst + ".new";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                fs.Write(data, 0, data.Length);
            if (!SameAsFile(tmp, data))
                throw new IOException("verification of " + name + " failed");

            if (File.Exists(dst)) File.Delete(dst);     // throws if the file is in use
            File.Move(tmp, dst);
        }

        static byte[] ReadResource(Assembly asm, string resName)
        {
            using (Stream s = asm.GetManifestResourceStream(resName))
            {
                if (s == null) throw new IOException("it is missing from this build of the exe");
                byte[] data = new byte[(int)s.Length];
                int got = 0;
                while (got < data.Length)
                {
                    int n = s.Read(data, got, data.Length - got);
                    if (n <= 0) throw new IOException("resource ended early");
                    got += n;
                }
                return data;
            }
        }

        static bool SameAsFile(string path, byte[] data)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length != data.Length) return false;
                    byte[] buf = new byte[16384];
                    int pos = 0;
                    while (pos < data.Length)
                    {
                        int n = fs.Read(buf, 0, buf.Length);
                        if (n <= 0) return false;
                        for (int i = 0; i < n; i++)
                            if (buf[i] != data[pos + i]) return false;
                        pos += n;
                    }
                    return true;
                }
            }
            catch (Exception) { return false; }
        }
    }
}
