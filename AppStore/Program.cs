using System;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace AppStore
{
    static class Program
    {
        // Main stays free of StoreForm and Https: the Compact Framework compiles a
        // whole method before running it, and those need CeHttpsNative.dll, which
        // Embedded.Extract() only unpacks once Main is running.
        [MTAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "/apply")
            {
                RunSelfUpdate();
                return;
            }
            if (!Embedded.Extract()) return;
            RunApp();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RunSelfUpdate()
        {
            try { Installer.ApplySelfUpdate(); }
            catch (Exception ex)
            {
                Log.Write("Self-update failed: " + ex.Message);
                MessageBox.Show("App Store update failed: " + ex.Message, "App Store");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RunApp()
        {
            Application.Run(new StoreForm());
        }
    }
}
