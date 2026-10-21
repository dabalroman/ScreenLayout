// ScreenLayout - switches between monitor layouts and keeps the chosen one applied.
//
// Windows stores one display layout per *set of connected monitors* and restores it on boot,
// logon and resume. A layout applied while a monitor is off/disconnected only updates the entry
// for that set, so the other set comes back with its old layout. The watcher remembers the chosen
// mode and re-applies it whenever the connected set changes, which also writes it into the
// Windows database for that set (so the logon screen follows it too).
//
//   ScreenLayout.exe set <mode>    remember mode (a section of layouts.ini), apply it, show a toast
//   ScreenLayout.exe watch         background watcher (started from the Startup folder)
//   ScreenLayout.exe init          setup: write layouts.ini from the monitors connected now (if
//                                  missing), add the Startup shortcut, start the watcher
//   ScreenLayout.exe status        log the current state
//   ScreenLayout.exe persist       save the active layout to the Windows database
//   ScreenLayout.exe restore-db    apply what the database holds (what Windows does on boot/resume)
//
// Monitors are addressed by Short Monitor ID throughout. Which monitors are on is set through the
// Windows CCD API; resolution, refresh rate, position and primary of the monitors that are then on
// through ChangeDisplaySettingsEx. That one takes \\.\DISPLAYn names, which shift when monitors
// are turned on/off, so they are looked up from the ID right before use and never stored.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenLayout
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        // Toast says "Switching..." while the layout is applied on a worker thread, then shows the result.
        static int Set(string mode)
        {
            Config cfg;
            try { cfg = Config.Load(Core.LayoutsFile); }
            catch (Exception e) { Core.Log("set: " + e.Message); return 2; }
            if (cfg.Mode(mode) == null) { Core.Log("set: unknown mode '" + mode + "'"); return 2; }
            Core.WriteMode(mode);

            Application.EnableVisualStyles();
            var toast = new Toast(cfg, mode, "Switching...");
            toast.Shown += (s, e) => new Thread(() =>
            {
                EnforceResult r = Core.Enforce(true, "hotkey");
                toast.BeginInvoke((Action)(() =>
                {
                    toast.SetDetail(r.Detail, !r.Ok);
                    toast.CloseAfter(r.Ok ? 2500 : 6000);
                }));
            }) { IsBackground = true }.Start();
            Application.Run(toast);
            return 0;
        }

        // Setup on a PC: layouts.ini (generated if missing), the Startup shortcut, the watcher (a no-op
        // if one is running; it reloads hotkeys itself), then layouts.ini in Notepad for editing.
        static int Init()
        {
            LayoutGenerator.Write();
            if (!File.Exists(Core.LayoutsFile)) return 1;
            try { Core.Log("init: startup shortcut " + StartupShortcut.Save()); }
            catch (Exception e) { Core.Log("init: startup shortcut failed: " + e.Message); }
            Process.Start(Application.ExecutablePath, "watch");
            Process.Start("notepad.exe", "\"" + Core.LayoutsFile + "\"");
            return 0;
        }

        [STAThread]
        static int Main(string[] args)
        {
            SetProcessDPIAware();
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (cmd)
            {
                case "set":
                    if (args.Length < 2) break;
                    return Set(args[1].ToLowerInvariant());

                case "watch":
                    bool first;
                    using (new Mutex(true, @"Local\ScreenLayoutWatcher", out first))
                    {
                        if (!first) return 0;
                        Core.Log("watcher started, mode=" + Core.ReadMode());
                        if (!File.Exists(Core.LayoutsFile)) LayoutGenerator.Write(); // first run on this PC
                        Application.EnableVisualStyles();
                        Application.Run(new Watcher());
                    }
                    return 0;

                case "preview":
                    if (args.Length < 2) break;
                    Application.EnableVisualStyles();
                    Toast.ShowResult(args[1].ToLowerInvariant(), "Preview - nothing switched", false, 5000);
                    if (Application.OpenForms.Count > 0) Application.Run(Application.OpenForms[0]);
                    return 0;

                case "init":
                    return Init();

                case "status":
                    Core.Log("status: mode=" + Core.ReadMode() + " " + Ccd.Query());
                    return 0;

                case "persist":
                    Core.Log("persist rc=" + Ccd.Persist());
                    return 0;

                case "restore-db":
                    Core.Log("restore-db rc=" + Ccd.RestoreFromDatabase());
                    return 0;
            }
            Core.Log("usage: set <mode> | watch | init | preview <mode> | status | persist | restore-db");
            return 2;
        }
    }
}
