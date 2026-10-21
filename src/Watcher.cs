using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenLayout
{
    // Hidden top-level window: receives display/device/session/power broadcasts, then enforces the
    // mode after things settle. At most 2 applies per mode+connected-monitors combination, so a
    // layout that cannot be reached does not loop; reset on unlock/logon.
    class Watcher : Form
    {
        const int WM_DISPLAYCHANGE = 0x7E;
        const int WM_POWERBROADCAST = 0x218;
        const int WM_DEVICECHANGE = 0x219;
        const int WM_WTSSESSION_CHANGE = 0x2B1;
        const int WTS_SESSION_LOGON = 5, WTS_SESSION_UNLOCK = 8;
        const int PBT_APMRESUMESUSPEND = 7, PBT_APMRESUMEAUTOMATIC = 0x12;
        const int WM_HOTKEY = 0x312;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        [DllImport("wtsapi32.dll")]
        static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        readonly Dictionary<int, string> hotkeyModes = new Dictionary<int, string>(); // hotkey id -> mode

        class PendingHotkey
        {
            public int Id;
            public uint Mods, Vk;
            public string Mode, Label;
        }

        readonly List<PendingHotkey> pendingHotkeys = new List<PendingHotkey>(); // taken by someone else, retried
        readonly System.Windows.Forms.Timer hotkeyRetry = new System.Windows.Forms.Timer { Interval = 30000 };
        readonly System.Windows.Forms.Timer reloadDelay = new System.Windows.Forms.Timer { Interval = 1000 };
        FileSystemWatcher layoutsWatcher;
        readonly Dictionary<string, int> attempts = new Dictionary<string, int>();
        DateTime quietUntil = DateTime.MinValue;
        string reason = "startup";

        public Watcher()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            CreateHandle();
            WTSRegisterSessionNotification(Handle, 0);
            hotkeyRetry.Tick += (o, e) => TryPendingHotkeys(false);
            RegisterHotkeys();
            WatchLayoutsFile();
            debounce.Tick += OnSettled;
            debounce.Interval = 5000; // first check after logon
            debounce.Start();
        }

        // Global hotkeys from layouts.ini ("hotkey = Ctrl+Alt+1"). Registered here rather than as
        // Start Menu shortcut hotkeys: Explorer drops those when the .lnk files are recreated.
        void RegisterHotkeys()
        {
            Config cfg;
            try { cfg = Config.Load(Core.LayoutsFile); }
            catch (Exception e) { Core.Log("hotkeys: " + e.Message); return; }
            int id = 1;
            foreach (ModeDef m in cfg.Modes.Where(md => md.Hotkey != null))
            {
                uint mods = MOD_NOREPEAT, vk = 0;
                foreach (string raw in m.Hotkey.Split('+'))
                {
                    string part = raw.Trim();
                    switch (part.ToLowerInvariant())
                    {
                        case "ctrl": case "control": mods |= MOD_CONTROL; break;
                        case "alt": mods |= MOD_ALT; break;
                        case "shift": mods |= MOD_SHIFT; break;
                        case "win": mods |= MOD_WIN; break;
                        default:
                            Keys key;
                            if (part.Length == 1 && char.IsLetterOrDigit(part[0])) vk = char.ToUpperInvariant(part[0]);
                            else if (Enum.TryParse(part, true, out key)) vk = (uint)key;
                            break;
                    }
                }
                if (vk == 0) { Core.Log("hotkeys: can't parse '" + m.Hotkey + "'"); continue; }
                pendingHotkeys.Add(new PendingHotkey { Id = id++, Mods = mods, Vk = vk, Mode = m.Key, Label = m.Hotkey });
            }
            TryPendingHotkeys(true);
            if (pendingHotkeys.Count > 0) hotkeyRetry.Start();
        }

        // Saving layouts.ini re-registers the hotkeys (after a second: editors write in several steps).
        // Mode lines need no reload, every apply reads the file.
        void WatchLayoutsFile()
        {
            reloadDelay.Tick += (o, e) =>
            {
                reloadDelay.Stop();
                foreach (int id in hotkeyModes.Keys) UnregisterHotKey(Handle, id);
                hotkeyModes.Clear();
                pendingHotkeys.Clear();
                hotkeyRetry.Stop();
                Core.Log("layouts.ini changed, reloading hotkeys");
                RegisterHotkeys();
            };
            FileSystemEventHandler changed = (o, e) => { reloadDelay.Stop(); reloadDelay.Start(); };
            layoutsWatcher = new FileSystemWatcher(Core.BaseDir, Path.GetFileName(Core.LayoutsFile))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                SynchronizingObject = this, // events on the UI thread
            };
            layoutsWatcher.Changed += changed;
            layoutsWatcher.Created += changed;
            layoutsWatcher.Renamed += (o, e) => changed(o, e);
            layoutsWatcher.EnableRaisingEvents = true;
        }

        // A hotkey that's taken (e.g. Explorer still holding a Start Menu shortcut's old hotkey until
        // it restarts) is retried every 30 s.
        void TryPendingHotkeys(bool first)
        {
            foreach (PendingHotkey h in pendingHotkeys.ToList())
            {
                if (RegisterHotKey(Handle, h.Id, h.Mods, h.Vk))
                {
                    hotkeyModes[h.Id] = h.Mode;
                    pendingHotkeys.Remove(h);
                    if (!first) Core.Log("hotkeys: " + h.Label + " registered");
                }
                else if (first)
                {
                    Core.Log("hotkeys: " + h.Label + " is taken by another program (error " + Marshal.GetLastWin32Error() + "), retrying every 30 s");
                }
            }
            if (first) Core.Log("hotkeys: registered " + string.Join(", ", hotkeyModes.Values));
            if (pendingHotkeys.Count == 0) hotkeyRetry.Stop();
        }

        // Runs "set <mode>" as its own process, so the toast and apply work exactly as from a shortcut.
        void OnHotkey(int id)
        {
            string mode;
            if (!hotkeyModes.TryGetValue(id, out mode)) return;
            try { Process.Start(Application.ExecutablePath, "set " + mode); }
            catch (Exception e) { Core.Log("hotkey " + mode + ": " + e.Message); }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        void Kick(string why)
        {
            reason = why;
            debounce.Stop();
            debounce.Interval = 3000;
            debounce.Start();
        }

        void OnSettled(object sender, EventArgs e)
        {
            debounce.Stop();
            if (DateTime.Now < quietUntil) { debounce.Start(); return; } // our own apply is still echoing

            string sig = Core.Signature();
            int n;
            attempts.TryGetValue(sig, out n);
            if (n >= 2) return;
            EnforceResult r = Core.Enforce(false, reason);
            if (r.Applied)
            {
                attempts[sig] = n + 1;
                quietUntil = DateTime.Now.AddSeconds(8);
                Toast.ShowResult(Core.ReadMode(), r.Ok ? "Restored after " + reason : r.Detail, !r.Ok, 3000);
            }
            else
            {
                attempts.Remove(sig);
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_HOTKEY: OnHotkey((int)m.WParam); break;
                case WM_DISPLAYCHANGE: Kick("display change"); break;
                case WM_DEVICECHANGE: Kick("device change"); break;
                case WM_POWERBROADCAST:
                    int pbt = (int)m.WParam;
                    if (pbt == PBT_APMRESUMESUSPEND || pbt == PBT_APMRESUMEAUTOMATIC) Kick("resume");
                    break;
                case WM_WTSSESSION_CHANGE:
                    int ev = (int)m.WParam;
                    if (ev == WTS_SESSION_UNLOCK || ev == WTS_SESSION_LOGON) { attempts.Clear(); Kick("unlock"); }
                    break;
            }
            base.WndProc(ref m);
        }
    }
}
