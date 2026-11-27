using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using CeHttps;

namespace AppStore
{
    public delegate void UiAction();

    /// <summary>
    /// The store: a list of apps from the catalog with what's installed and
    /// what's available. Up/down to pick, the right softkey (or Enter) does the
    /// obvious thing: Install, Update or Open.
    /// </summary>
    public class StoreForm : Form
    {
        readonly Label status;
        readonly ListView list;
        readonly TextBox info;
        readonly MenuItem actionKey, installItem, uninstallItem, openItem, notesItem;

        List<CatalogApp> rows = new List<CatalogApp>();
        List<CatalogApp> catalogApps = new List<CatalogApp>();
        string catalogTitle = "";
        bool ready, busy, closing;
        bool clockChecked;      // NTP answered at least once this run

        public StoreForm()
        {
            Text = "App Store";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            // Laid out by hand in LayoutControls: docking put the text box
            // behind the list on the device.
            status = new Label();
            status.Text = "Starting...";

            list = new ListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.Columns.Add("App", 112, HorizontalAlignment.Left);
            list.Columns.Add("Installed", 58, HorizontalAlignment.Left);
            list.Columns.Add("Latest", 58, HorizontalAlignment.Left);
            list.SelectedIndexChanged += delegate { ShowSelected(); };
            list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; DoAction(); }
            };

            info = new TextBox();
            info.Multiline = true;
            info.ReadOnly = true;
            info.ScrollBars = ScrollBars.Vertical;
            info.WordWrap = true;

            Controls.Add(status);
            Controls.Add(list);
            Controls.Add(info);
            Resize += delegate { LayoutControls(); };

            MainMenu menu = new MainMenu();
            MenuItem left = new MenuItem();
            left.Text = "Menu";
            installItem = Item("Install", delegate { InstallSelected(); });
            uninstallItem = Item("Uninstall", delegate { UninstallSelected(); });
            openItem = Item("Open", delegate { OpenSelected(); });
            notesItem = Item("Release notes", delegate { ShowNotes(); });
            left.MenuItems.Add(Item("Refresh", delegate { RefreshCatalog(); }));
            left.MenuItems.Add(installItem);
            left.MenuItems.Add(uninstallItem);
            left.MenuItems.Add(openItem);
            left.MenuItems.Add(notesItem);
            left.MenuItems.Add(Item("Show log", delegate { info.Text = Log.Tail(40); ScrollInfoToEnd(); }));
            left.MenuItems.Add(Item("About", delegate { ShowAbout(); }));
            left.MenuItems.Add(Item("Exit", delegate { Close(); }));
            actionKey = Item("", delegate { DoAction(); });
            menu.MenuItems.Add(left);
            menu.MenuItems.Add(actionKey);
            Menu = menu;
            UpdateActions();

            Load += delegate { LayoutControls(); list.Focus(); StartUp(); };
            Closing += delegate { closing = true; };
        }

        // Status line on top, the list just tall enough for its rows (at most
        // half the screen, then it scrolls), and the text gets the rest.
        void LayoutControls()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            int line;
            using (Graphics g = CreateGraphics())
                line = (int)Math.Ceiling(g.MeasureString("Ag", Font).Height);
            int gap = Math.Max(2, line / 6);

            int statusH = line + gap;
            status.Bounds = new Rectangle(gap, 0, w - 2 * gap, statusH);

            int rowH = line + 2 * gap;
            int shown = Math.Max(3, rows.Count);
            int listH = Math.Min(rowH * (shown + 1) + gap * 2, (h - statusH) / 2);
            list.Bounds = new Rectangle(0, statusH, w, listH);

            int top = statusH + listH + gap;
            info.Bounds = new Rectangle(0, top, w, Math.Max(line * 2, h - top));

            // Name column takes what the two version columns leave.
            if (list.Columns.Count == 3)
            {
                int ver = (int)(w * 0.24);
                list.Columns[1].Width = ver;
                list.Columns[2].Width = ver;
                list.Columns[0].Width = Math.Max(40, w - 2 * ver - line - 4);
            }
        }

        static MenuItem Item(string text, EventHandler click)
        {
            MenuItem m = new MenuItem();
            m.Text = text;
            m.Click += click;
            return m;
        }

        // ------------------------------------------------------------- start-up and refresh

        void StartUp()
        {
            Installer.CleanStaging();
            busy = true;
            Background(delegate
            {
                try
                {
                    byte[] ca = Files.Read(Path.Combine(Store.AppDir, "cacert.pem"), 4 * 1024 * 1024);
                    Https.Init(ca);
                    Https.UserAgent = "AppStore/" + Store.VersionText.Substring(1) + " (Windows CE)";
                    ready = true;
                    Ui(delegate { status.Text = "Checking the clock..."; });
                    FixClock();
                }
                catch (Exception ex)
                {
                    Ui(delegate { busy = false; Tell("Couldn't start secure connections: " + ex.Message); });
                    return;
                }
                Ui(delegate { busy = false; RefreshCatalog(); });
            });
        }

        void RefreshCatalog()
        {
            if (!ready || busy) return;
            busy = true;
            UpdateActions();
            status.Text = "Loading the catalog...";
            Background(delegate
            {
                List<CatalogApp> apps;
                string title;
                List<InstalledApp> installed = Installer.FindInstalled();
                int repaired = Installer.RepairShortcuts(installed);
                // Wi-Fi may only have come up after start-up: try the clock again.
                if (!clockChecked || Https.ClockLooksWrong) FixClock();
                try { apps = Catalog.Fetch(out title); }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    if (Https.ClockLooksWrong)
                        msg = "The date on this device is wrong (it says " + DateTime.Now.ToString("yyyy-MM-dd") +
                              "), so secure connections fail.\r\nConnect to Wi-Fi and use Menu > Refresh to set it " +
                              "automatically, or set the date and time in Settings.";
                    Ui(delegate
                    {
                        busy = false;
                        status.Text = "Catalog not loaded";
                        ShowRows(Orphans(new List<CatalogApp>(), installed));
                        Tell("Couldn't load the catalog from " + Store.CatalogSource + ":\r\n" + msg);
                    });
                    return;
                }

                foreach (CatalogApp a in apps)
                    foreach (InstalledApp i in installed)
                        if (i.Id == a.Id) a.Installed = i;
                List<CatalogApp> all = Orphans(apps, installed);
                Ui(delegate
                {
                    catalogTitle = title;
                    catalogApps = apps;
                    ShowRows(all);
                    status.Text = "Checking " + apps.Count + " apps...";
                });

                foreach (CatalogApp a in apps)
                {
                    if (closing) return;
                    try { a.Latest = Catalog.LatestRelease(a.Repo); if (a.Latest == null) a.LatestError = "no release yet"; }
                    catch (Exception ex) { a.LatestError = ex.Message; }
                    CatalogApp row = a;
                    Ui(delegate { UpdateRow(row); });
                }

                Ui(delegate
                {
                    busy = false;
                    int updates = 0;
                    foreach (CatalogApp a in rows) if (a.UpdateAvailable) updates++;
                    status.Text = (catalogTitle.Length > 0 ? catalogTitle : "App Store") +
                        (updates > 0 ? " - " + updates + (updates == 1 ? " update" : " updates") + " (*)" : "");
                    if (repaired > 0) Log.Write("Restored " + repaired + " shortcut(s) after a clean boot");
                    UpdateActions();
                    ShowSelected();
                });
            });
        }

        // After an install or uninstall: re-read what's on the device (no network).
        void RescanInstalled()
        {
            List<InstalledApp> installed = Installer.FindInstalled();
            foreach (CatalogApp a in catalogApps)
            {
                a.Installed = null;
                foreach (InstalledApp i in installed) if (i.Id == a.Id) a.Installed = i;
            }
            ShowRows(Orphans(catalogApps, installed));
        }

        /// <summary>
        /// Sets the clock from NTP when it's off. Certificates are date-checked,
        /// and roots loaded while the date was wrong stay rejected, so reload
        /// them after a fix. Runs on a background thread.
        /// </summary>
        void FixClock()
        {
            string result = ClockSync.Sync();
            Log.Write(result);
            if (!result.StartsWith("Clock not checked")) clockChecked = true;
            if (result.StartsWith("Clock corrected") || (clockChecked && Https.ClockLooksWrong))
            {
                try { Https.Reload(); }
                catch (Exception ex) { Log.Write("Reloading certificates failed: " + ex.Message); }
                int ok, bad, dateBad;
                Https.RootStats(out ok, out bad, out dateBad);
                Log.Write("Certificates reloaded: " + ok + " trusted, " + dateBad + " rejected by date");
            }
        }

        // Installed apps that aren't in the catalog (any more) still get a row,
        // so they can be uninstalled.
        static List<CatalogApp> Orphans(List<CatalogApp> apps, List<InstalledApp> installed)
        {
            List<CatalogApp> all = new List<CatalogApp>(apps);
            foreach (InstalledApp i in installed)
            {
                bool listed = false;
                foreach (CatalogApp a in apps) if (a.Id == i.Id) listed = true;
                if (listed) continue;
                CatalogApp o = new CatalogApp();
                o.Id = i.Id;
                o.Name = i.Name;
                o.Description = "Installed, but no longer in the catalog.";
                o.Installed = i;
                o.LatestError = "not in catalog";
                all.Add(o);
            }
            return all;
        }

        void ShowRows(List<CatalogApp> apps)
        {
            int keep = SelectedIndex;
            rows = apps;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (CatalogApp a in apps)
            {
                ListViewItem item = new ListViewItem(a.Name);
                item.SubItems.Add(InstalledText(a));
                item.SubItems.Add(LatestText(a));
                list.Items.Add(item);
            }
            list.EndUpdate();
            LayoutControls();
            if (rows.Count > 0)
            {
                int sel = keep >= 0 && keep < rows.Count ? keep : 0;
                list.Items[sel].Selected = true;
            }
            UpdateActions();
            ShowSelected();
        }

        void UpdateRow(CatalogApp a)
        {
            int i = rows.IndexOf(a);
            if (i < 0 || i >= list.Items.Count) return;
            list.Items[i].SubItems[1].Text = InstalledText(a);
            list.Items[i].SubItems[2].Text = LatestText(a);
            if (i == SelectedIndex) { UpdateActions(); ShowSelected(); }
        }

        static string InstalledText(CatalogApp a)
        {
            return a.Installed == null ? "-" : Installer.InstalledVersionText(a.Installed);
        }

        static string LatestText(CatalogApp a)
        {
            if (a.Latest != null) return a.Latest.Tag + (a.UpdateAvailable ? " *" : "");
            if (a.LatestError != null) return a.LatestError == "not in catalog" ? "" : "?";
            return "...";
        }

        // ------------------------------------------------------------- selection and actions

        int SelectedIndex
        {
            get { return list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1; }
        }

        CatalogApp Selected
        {
            get { int i = SelectedIndex; return i >= 0 && i < rows.Count ? rows[i] : null; }
        }

        void ShowSelected()
        {
            if (busy && info.Text.Length > 0 && info.Text.EndsWith("...")) return;   // don't hide progress
            CatalogApp a = Selected;
            if (a == null) { info.Text = rows.Count == 0 ? "" : "Pick an app."; return; }
            string text = a.Name + (a.Repo.Length > 0 ? "  (" + a.Repo + ")" : "");
            if (a.Description.Length > 0) text += "\r\n" + a.Description;
            if (a.Installed != null) text += "\r\nInstalled in " + a.Installed.Folder;
            if (a.UpdateAvailable) text += "\r\nUpdate available: " + a.Latest.Tag;
            if (a.LatestError != null && a.LatestError != "not in catalog") text += "\r\nCan't check GitHub: " + a.LatestError;
            info.Text = text;
        }

        void UpdateActions()
        {
            CatalogApp a = Selected;
            bool canInstall = !busy && a != null && a.Latest != null && (a.Installed == null || a.UpdateAvailable);
            bool reinstall = !busy && a != null && a.Latest != null && a.Installed != null && !a.UpdateAvailable;
            installItem.Text = a != null && a.Installed != null ? (a.UpdateAvailable ? "Update" : "Reinstall") : "Install";
            installItem.Enabled = canInstall || reinstall;
            uninstallItem.Enabled = !busy && a != null && a.Installed != null;
            openItem.Enabled = !busy && a != null && a.Installed != null && File.Exists(a.Installed.ExePath);
            notesItem.Enabled = a != null && a.Latest != null;

            if (busy) actionKey.Text = "";
            else if (canInstall) actionKey.Text = a.Installed == null ? "Install" : "Update";
            else if (openItem.Enabled) actionKey.Text = "Open";
            else actionKey.Text = "";
            actionKey.Enabled = actionKey.Text.Length > 0;
        }

        void DoAction()
        {
            if (busy) return;
            CatalogApp a = Selected;
            if (a == null) return;
            if (a.Latest != null && (a.Installed == null || a.UpdateAvailable)) InstallSelected();
            else if (a.Installed != null) OpenSelected();
        }

        void InstallSelected()
        {
            CatalogApp a = Selected;
            if (busy || a == null || a.Latest == null) return;

            List<ReleaseAsset> assets;
            try { assets = Installer.PickAssets(a, a.Latest); }
            catch (Exception ex) { Tell(ex.Message); return; }
            int total = 0;
            foreach (ReleaseAsset x in assets) total += x.Size;

            string verb = a.Installed == null ? "Install" : a.UpdateAvailable ? "Update" : "Reinstall";
            string where = a.Installed != null ? a.Installed.Folder : Path.Combine(Installer.InstallRoot, a.Folder);
            string question = verb + " " + a.Name + " " + a.Latest.Tag + "?\r\n\r\n" +
                (total > 0 ? "Download: " + Installer.Kb(total) + "\r\n" : "") +
                "Folder: " + where +
                (a.InstallArgs.Length > 0 ? "\r\nThen runs: " + (a.Exe.Length > 0 ? a.Exe : "the app") + " " + a.InstallArgs : "");
            if (MessageBox.Show(question, verb, MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1) != DialogResult.OK) return;

            busy = true;
            UpdateActions();
            CatalogApp app = a;
            Background(delegate
            {
                InstallResult result = null;
                string error = null;
                try { result = Installer.Install(app, delegate(string m) { Ui(delegate { info.Text = m; }); }); }
                catch (Exception ex) { error = ex.Message; Log.Write("Install of " + app.Name + " failed: " + ex.Message); }
                Ui(delegate
                {
                    busy = false;
                    if (error != null) { Tell(app.Name + ": " + error); UpdateActions(); return; }
                    if (result.StoreMustExit)
                    {
                        MessageBox.Show(result.Message, "App Store");
                        closing = true;
                        Close();
                        return;
                    }
                    RescanInstalled();
                    Tell(result.Message);
                });
            });
        }

        void UninstallSelected()
        {
            CatalogApp a = Selected;
            if (busy || a == null || a.Installed == null) return;
            InstalledApp inst = a.Installed;
            string question = "Uninstall " + a.Name + "?\r\n\r\nRemoves the files App Store installed in " + inst.Folder +
                (inst.Shortcut.Length > 0 ? " and its Start menu shortcut" : "") +
                ". Settings and data you made are kept." +
                (inst.UninstallArgs.Length > 0 ? "\r\nFirst runs: " + inst.Exe + " " + inst.UninstallArgs : "");
            if (MessageBox.Show(question, "Uninstall", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            busy = true;
            UpdateActions();
            Background(delegate
            {
                string message;
                try { message = Installer.Uninstall(inst, delegate(string m) { Ui(delegate { info.Text = m; }); }); }
                catch (Exception ex) { message = "Couldn't uninstall " + a.Name + ": " + ex.Message; Log.Write(message); }
                Ui(delegate { busy = false; RescanInstalled(); Tell(message); });
            });
        }

        void OpenSelected()
        {
            CatalogApp a = Selected;
            if (a == null || a.Installed == null) return;
            try { Process.Start(a.Installed.ExePath, ""); }
            catch (Exception ex) { Tell("Couldn't start " + a.Installed.ExePath + ": " + ex.Message); }
        }

        void ShowNotes()
        {
            CatalogApp a = Selected;
            if (a == null || a.Latest == null) return;
            string date = a.Latest.Published.Length >= 10 ? " (" + a.Latest.Published.Substring(0, 10) + ")" : "";
            string notes = a.Latest.Notes.Trim().Replace("\r\n", "\n").Replace("\n", "\r\n");
            info.Text = a.Name + " " + a.Latest.Tag + date + "\r\n" + (notes.Length > 0 ? notes : "(no release notes)");
        }

        void ShowAbout()
        {
            MessageBox.Show("App Store " + Store.VersionText +
                "\r\nCatalog: " + Store.CatalogSource +
                "\r\nInstalls to: " + Installer.InstallRoot +
                "\r\nRunning from: " + Store.AppDir +
                "\r\n\r\nSecure connections by CeHttps (wolfSSL).", "About");
        }

        // ------------------------------------------------------------- helpers

        void Tell(string message)
        {
            Log.Write(message);
            info.Text = message;
        }

        void ScrollInfoToEnd()
        {
            try { info.SelectionStart = info.Text.Length; info.ScrollToCaret(); }
            catch (Exception) { }
        }

        void Background(UiAction work)
        {
            Thread t = new Thread(delegate()
            {
                try { work(); }
                catch (Exception ex) { Log.Write("Unexpected error: " + ex); Ui(delegate { busy = false; UpdateActions(); Tell("Unexpected error: " + ex.Message); }); }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Ui(UiAction a)
        {
            if (closing) return;
            try
            {
                if (InvokeRequired) Invoke(a);
                else a();
            }
            catch (ObjectDisposedException) { }
        }
    }
}
