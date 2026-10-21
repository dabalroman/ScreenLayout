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
//   ScreenLayout.exe init          write layouts.ini from the monitors connected now (if missing)
//   ScreenLayout.exe status        log the current state
//   ScreenLayout.exe persist       save the active layout to the Windows database
//   ScreenLayout.exe restore-db    apply what the database holds (what Windows does on boot/resume)
//
// Monitors are addressed by Short Monitor ID throughout. Which monitors are on is set through the
// Windows CCD API; resolution, refresh rate, position and primary of the monitors that are then on
// through ChangeDisplaySettingsEx. That one takes \\.\DISPLAYn names, which shift when monitors
// are turned on/off, so they are looked up from the ID right before use and never stored.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

class MonitorSpec
{
    public string Id;
    public int Width, Height, Hz, X, Y;
}

class ModeDef
{
    public string Key, Name, Hotkey, Primary;
    public List<MonitorSpec> Active = new List<MonitorSpec>();
    public List<string> Disabled = new List<string>(); // known monitors not in this mode
}

class Config
{
    public List<ModeDef> Modes = new List<ModeDef>();
    public Dictionary<string, string> MonitorNames = new Dictionary<string, string>();
    public List<string> MonitorOrder = new List<string>(); // [monitors] order = left to right in the toast

    public ModeDef Mode(string key)
    {
        return Modes.FirstOrDefault(m => m.Key == key);
    }

    // Resolution of a monitor as used in any mode (for the toast's diagram).
    public MonitorSpec Spec(string id)
    {
        return Modes.SelectMany(m => m.Active).FirstOrDefault(s => s.Id == id);
    }

    public string MonitorName(string id)
    {
        string name;
        return MonitorNames.TryGetValue(id, out name) ? name : id;
    }

    static readonly Regex SpecRe = new Regex(@"^(\d+)x(\d+)@(\d+)\s+(-?\d+),(-?\d+)(\s+primary)?$", RegexOptions.IgnoreCase);

    public static Config Load(string file)
    {
        var cfg = new Config();
        var known = new List<string>();
        string section = null;
        ModeDef cur = null;
        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                cur = null;
                if (section != "monitors")
                {
                    cur = new ModeDef { Key = section, Name = section };
                    cfg.Modes.Add(cur);
                }
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || section == null) continue;
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();

            if (cur == null)
            {
                string mid = key.ToUpperInvariant();
                cfg.MonitorNames[mid] = value;
                if (!cfg.MonitorOrder.Contains(mid)) cfg.MonitorOrder.Add(mid);
                continue;
            }
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) { cur.Name = value; continue; }
            if (key.Equals("hotkey", StringComparison.OrdinalIgnoreCase)) { cur.Hotkey = value; continue; }

            Match m = SpecRe.Match(value);
            if (!m.Success) throw new Exception("layouts.ini: bad line '" + line + "'");
            string id = key.ToUpperInvariant();
            cur.Active.Add(new MonitorSpec
            {
                Id = id,
                Width = int.Parse(m.Groups[1].Value),
                Height = int.Parse(m.Groups[2].Value),
                Hz = int.Parse(m.Groups[3].Value),
                X = int.Parse(m.Groups[4].Value),
                Y = int.Parse(m.Groups[5].Value),
            });
            if (m.Groups[6].Success) cur.Primary = id;
            if (!known.Contains(id)) known.Add(id);
        }
        cfg.Modes.RemoveAll(md => md.Active.Count == 0);
        foreach (var md in cfg.Modes)
        {
            if (md.Primary == null) md.Primary = md.Active[0].Id;
            md.Disabled = known.Where(id => !md.Active.Any(s => s.Id == id)).ToList();
        }
        return cfg;
    }
}

// A connected monitor as found by Ccd.Monitors(): current mode if on, preferred mode if off.
class MonitorInfo
{
    public string Id, Name;
    public bool Active, Primary;
    public int Width, Height, Hz, X, Y;
}

class DisplayState
{
    public HashSet<string> Connected = new HashSet<string>();
    public Dictionary<string, int> ActiveHz = new Dictionary<string, int>();
    public string Primary;

    public override string ToString()
    {
        return "connected=[" + string.Join(",", Connected.OrderBy(s => s)) + "] active=[" +
            string.Join(",", ActiveHz.OrderBy(kv => kv.Key).Select(kv => kv.Key + "@" + kv.Value)) + "] primary=" + Primary;
    }
}

static class Ccd
{
    const uint QDC_ALL_PATHS = 0x1;
    const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    const uint SDC_USE_DATABASE_CURRENT = 0xF;
    const uint SDC_TOPOLOGY_SUPPLIED = 0x10;
    const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    const uint SDC_APPLY = 0x80;
    const uint SDC_SAVE_TO_DATABASE = 0x200;
    const uint SDC_ALLOW_CHANGES = 0x400;
    const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;
    const int PATH_INFO_SIZE = 72;    // sizeof(DISPLAYCONFIG_PATH_INFO)
    const int MODE_INFO_SIZE = 64;    // sizeof(DISPLAYCONFIG_MODE_INFO)
    const int TARGET_NAME_SIZE = 420; // sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME)
    const int SOURCE_NAME_SIZE = 84;  // sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME)

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(IntPtr packet);

    // Runs fn over a QueryDisplayConfig snapshot; retries when the layout changes in between.
    static T WithConfig<T>(uint flags, Func<uint, IntPtr, uint, IntPtr, T> fn)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            uint numPaths, numModes;
            int rc = GetDisplayConfigBufferSizes(flags, out numPaths, out numModes);
            if (rc != 0) throw new Exception("GetDisplayConfigBufferSizes rc=" + rc);
            IntPtr paths = Marshal.AllocHGlobal((int)Math.Max(1, numPaths) * PATH_INFO_SIZE);
            IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, numModes) * MODE_INFO_SIZE);
            try
            {
                rc = QueryDisplayConfig(flags, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                if (rc == 122) continue; // ERROR_INSUFFICIENT_BUFFER
                if (rc != 0) throw new Exception("QueryDisplayConfig rc=" + rc);
                return fn(numPaths, paths, numModes, modes);
            }
            finally
            {
                Marshal.FreeHGlobal(paths);
                Marshal.FreeHGlobal(modes);
            }
        }
        throw new Exception("QueryDisplayConfig kept changing");
    }

    // Zeroed DisplayConfigGetDeviceInfo packet: type, size, adapter LUID, source/target id.
    static IntPtr InfoPacket(int type, int size, long adapterId, uint id)
    {
        IntPtr pkt = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(pkt, i, 0);
        Marshal.WriteInt32(pkt, 0, type);
        Marshal.WriteInt32(pkt, 4, size);
        Marshal.WriteInt64(pkt, 8, adapterId);
        Marshal.WriteInt32(pkt, 16, (int)id);
        return pkt;
    }

    // { Short monitor ID (e.g. IVM7628) from the target's device path \\?\DISPLAY#IVM7628#...,
    // EDID friendly name at offset 36 }, or null if the target has no monitor.
    static string[] TargetId(long adapterId, uint targetId)
    {
        IntPtr pkt = InfoPacket(2, TARGET_NAME_SIZE, adapterId, targetId); // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
        try
        {
            if (DisplayConfigGetDeviceInfo(pkt) != 0) return null;
            string path = Marshal.PtrToStringUni(pkt + 164);
            string[] parts = (path ?? "").Split('#');
            if (parts.Length < 2 || parts[1].Length == 0) return null;
            string id = parts[1].ToUpperInvariant();
            string name = (Marshal.PtrToStringUni(pkt + 36) ?? "").Trim();
            return new[] { id, name.Length > 0 ? name : id };
        }
        finally
        {
            Marshal.FreeHGlobal(pkt);
        }
    }

    // { width, height, Hz } of a target's preferred (native) mode. DISPLAYCONFIG_TARGET_PREFERRED_MODE
    // is 80 bytes: header 20, width 20, height 24, targetMode at 32 (its vSyncFreq at 48).
    static int[] PreferredMode(long adapterId, uint targetId)
    {
        IntPtr pkt = InfoPacket(3, 80, adapterId, targetId); // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE
        try
        {
            if (DisplayConfigGetDeviceInfo(pkt) != 0) return null;
            uint num = (uint)Marshal.ReadInt32(pkt, 48), den = (uint)Marshal.ReadInt32(pkt, 52);
            return new[] { Marshal.ReadInt32(pkt, 20), Marshal.ReadInt32(pkt, 24), den == 0 ? 0 : (int)Math.Round((double)num / den) };
        }
        finally
        {
            Marshal.FreeHGlobal(pkt);
        }
    }

    // Per path: monitor ID (cached per target), target available, path active.
    class PathView
    {
        public IntPtr Ptr;
        public string Source, Id, Name;
        public long Adapter;
        public uint Target;
        public bool Available, Active;
    }

    static List<PathView> Paths(uint numPaths, IntPtr paths)
    {
        var ids = new Dictionary<string, string[]>();
        var list = new List<PathView>();
        for (int i = 0; i < numPaths; i++)
        {
            IntPtr p = paths + i * PATH_INFO_SIZE;
            long adapter = Marshal.ReadInt64(p, 20);
            uint target = (uint)Marshal.ReadInt32(p, 28);
            var v = new PathView
            {
                Ptr = p,
                Adapter = adapter,
                Target = target,
                Source = Marshal.ReadInt64(p, 0) + ":" + Marshal.ReadInt32(p, 8),
                Available = Marshal.ReadInt32(p, 60) != 0,
                Active = (Marshal.ReadInt32(p, 68) & 1) != 0,
            };
            if (!v.Available && !v.Active) continue;
            string key = adapter + ":" + target;
            string[] id;
            if (!ids.TryGetValue(key, out id)) ids[key] = id = TargetId(adapter, target);
            if (id == null) continue;
            v.Id = id[0];
            v.Name = id[1];
            list.Add(v);
        }
        return list;
    }

    public static DisplayState Query()
    {
        return WithConfig(QDC_ALL_PATHS, (numPaths, paths, numModes, modes) =>
        {
            var st = new DisplayState();
            foreach (var v in Paths(numPaths, paths))
            {
                st.Connected.Add(v.Id);
                if (!v.Active) continue;
                uint num = (uint)Marshal.ReadInt32(v.Ptr, 48), den = (uint)Marshal.ReadInt32(v.Ptr, 52);
                st.ActiveHz[v.Id] = den == 0 ? 0 : (int)Math.Round((double)num / den);
                uint srcMode = (uint)Marshal.ReadInt32(v.Ptr, 12);
                if (srcMode < numModes)
                {
                    IntPtr m = modes + (int)srcMode * MODE_INFO_SIZE;
                    if (Marshal.ReadInt32(m, 28) == 0 && Marshal.ReadInt32(m, 32) == 0) st.Primary = v.Id;
                }
            }
            return st;
        });
    }

    // Every connected monitor once, with its current mode (on) or preferred mode (off).
    public static List<MonitorInfo> Monitors()
    {
        return WithConfig(QDC_ALL_PATHS, (numPaths, paths, numModes, modes) =>
        {
            var list = new List<MonitorInfo>();
            var all = Paths(numPaths, paths);
            foreach (var v in all.Where(p => p.Active).Concat(all.Where(p => !p.Active)))
            {
                if (list.Any(mi => mi.Id == v.Id)) continue;
                var info = new MonitorInfo { Id = v.Id, Name = v.Name, Active = v.Active };
                uint srcMode = (uint)Marshal.ReadInt32(v.Ptr, 12);
                if (v.Active && srcMode < numModes)
                {
                    IntPtr m = modes + (int)srcMode * MODE_INFO_SIZE;
                    info.Width = Marshal.ReadInt32(m, 16);
                    info.Height = Marshal.ReadInt32(m, 20);
                    info.X = Marshal.ReadInt32(m, 28);
                    info.Y = Marshal.ReadInt32(m, 32);
                    info.Primary = info.X == 0 && info.Y == 0;
                    uint num = (uint)Marshal.ReadInt32(v.Ptr, 48), den = (uint)Marshal.ReadInt32(v.Ptr, 52);
                    info.Hz = den == 0 ? 0 : (int)Math.Round((double)num / den);
                }
                else
                {
                    info.Active = false;
                    int[] pref = PreferredMode(v.Adapter, v.Target);
                    if (pref == null) continue;
                    info.Width = pref[0];
                    info.Height = pref[1];
                    info.Hz = pref[2];
                }
                list.Add(info);
            }
            return list;
        });
    }

    // GDI device name (\\.\DISPLAYn) of each active monitor's source. Valid only until the next
    // topology change, so it is looked up right before use.
    public static Dictionary<string, string> GdiNames()
    {
        return WithConfig(QDC_ONLY_ACTIVE_PATHS, (numPaths, paths, numModes, modes) =>
        {
            var names = new Dictionary<string, string>();
            foreach (var v in Paths(numPaths, paths))
            {
                if (!v.Active || names.ContainsKey(v.Id)) continue;
                // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME for the path's source (adapter at 0, id at 8)
                IntPtr pkt = InfoPacket(1, SOURCE_NAME_SIZE, Marshal.ReadInt64(v.Ptr, 0), (uint)Marshal.ReadInt32(v.Ptr, 8));
                try
                {
                    if (DisplayConfigGetDeviceInfo(pkt) == 0) names[v.Id] = Marshal.PtrToStringUni(pkt + 20);
                }
                finally
                {
                    Marshal.FreeHGlobal(pkt);
                }
            }
            return names;
        });
    }

    // Turns on exactly the given monitors (one path each, distinct sources; currently active paths
    // are kept). Windows picks the modes; positions/refresh are set afterwards.
    public static int SetActive(ICollection<string> wanted)
    {
        return WithConfig(QDC_ALL_PATHS, (numPaths, paths, numModes, modes) =>
        {
            var all = Paths(numPaths, paths);
            var chosen = new List<PathView>();
            var usedSources = new HashSet<string>();
            var usedIds = new HashSet<string>();
            foreach (bool activeOnly in new[] { true, false })
                foreach (var v in all)
                {
                    if (activeOnly && !v.Active) continue;
                    if (!v.Available || !wanted.Contains(v.Id) || usedIds.Contains(v.Id) || usedSources.Contains(v.Source)) continue;
                    chosen.Add(v);
                    usedIds.Add(v.Id);
                    usedSources.Add(v.Source);
                }
            if (chosen.Count == 0) return 87; // ERROR_INVALID_PARAMETER

            IntPtr buf = Marshal.AllocHGlobal(chosen.Count * PATH_INFO_SIZE);
            try
            {
                var tmp = new byte[PATH_INFO_SIZE];
                for (int i = 0; i < chosen.Count; i++)
                {
                    Marshal.Copy(chosen[i].Ptr, tmp, 0, PATH_INFO_SIZE);
                    IntPtr p = buf + i * PATH_INFO_SIZE;
                    Marshal.Copy(tmp, 0, p, PATH_INFO_SIZE);
                    Marshal.WriteInt32(p, 12, -1); // source modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                    Marshal.WriteInt32(p, 32, -1); // target modeInfoIdx
                    Marshal.WriteInt32(p, 68, Marshal.ReadInt32(p, 68) | 1); // DISPLAYCONFIG_PATH_ACTIVE
                }
                int rc = SetDisplayConfig((uint)chosen.Count, buf, 0, IntPtr.Zero,
                    SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
                if (rc == 0) return 0;
                int rc2 = SetDisplayConfig((uint)chosen.Count, buf, 0, IntPtr.Zero,
                    SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES);
                Core.Log("SetActive: supplied config rc=" + rc + ", topology supplied rc=" + rc2);
                return rc2;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        });
    }

    public static int Persist()
    {
        return WithConfig(QDC_ONLY_ACTIVE_PATHS, (numPaths, paths, numModes, modes) =>
            SetDisplayConfig(numPaths, paths, numModes, modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES));
    }

    public static int RestoreFromDatabase()
    {
        return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_USE_DATABASE_CURRENT);
    }
}

class EnforceResult
{
    public bool Applied, Ok;
    public string Detail;
}

// Resolution, refresh rate, position and primary of monitors that are on, via
// ChangeDisplaySettingsEx: every monitor is staged with CDS_NORESET, then all applied at once.
static class Gdi
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE // DEVMODEW, display variant (220 bytes)
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    const int DM_POSITION = 0x20, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const uint CDS_UPDATEREGISTRY = 0x1, CDS_SET_PRIMARY = 0x10, CDS_NORESET = 0x10000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE mode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, IntPtr mode, IntPtr hwnd, uint flags, IntPtr param);

    static DEVMODE NewMode()
    {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        return dm;
    }

    // The refresh rate the driver lists for this resolution that is closest to hz (59.94 Hz modes
    // are listed as 59, so "60" alone may not exist).
    static int ClosestHz(string device, int width, int height, int hz)
    {
        int best = hz, diff = int.MaxValue;
        DEVMODE dm = NewMode();
        for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
        {
            if (dm.dmPelsWidth != width || dm.dmPelsHeight != height) continue;
            int d = Math.Abs(dm.dmDisplayFrequency - hz);
            if (d < diff) { diff = d; best = dm.dmDisplayFrequency; }
        }
        return best;
    }

    // Positions in want are relative to its primary at 0,0 (see Core.Effective).
    public static void SetModes(ModeDef want)
    {
        Dictionary<string, string> devices = Ccd.GdiNames();
        foreach (MonitorSpec sp in want.Active.OrderBy(m => m.Id == want.Primary ? 0 : 1))
        {
            string device;
            if (!devices.TryGetValue(sp.Id, out device)) { Core.Log("  " + sp.Id + " is not on, mode not set"); continue; }
            DEVMODE dm = NewMode();
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            dm.dmPositionX = sp.X;
            dm.dmPositionY = sp.Y;
            dm.dmPelsWidth = sp.Width;
            dm.dmPelsHeight = sp.Height;
            dm.dmDisplayFrequency = ClosestHz(device, sp.Width, sp.Height, sp.Hz);
            uint flags = CDS_UPDATEREGISTRY | CDS_NORESET | (sp.Id == want.Primary ? CDS_SET_PRIMARY : 0);
            int rc = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (rc != 0) Core.Log("  " + sp.Id + " " + sp.Width + "x" + sp.Height + "@" + dm.dmDisplayFrequency + ": ChangeDisplaySettingsEx rc=" + rc);
        }
        int rc2 = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (rc2 != 0) Core.Log("  ChangeDisplaySettingsEx apply rc=" + rc2);
    }
}

static class Core
{
    public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string ModeFile = Path.Combine(BaseDir, "mode.txt");
    public static readonly string LayoutsFile = Path.Combine(BaseDir, "layouts.ini");
    static readonly string LogFile = Path.Combine(BaseDir, "ScreenLayout.log");

    public static void Log(string msg)
    {
        try
        {
            var fi = new FileInfo(LogFile);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(LogFile, DateTime.Now.ToString("s") + " " + msg + Environment.NewLine);
        }
        catch { }
    }

    public static string ReadMode()
    {
        try { return File.ReadAllText(ModeFile).Trim().ToLowerInvariant(); }
        catch { return null; }
    }

    public static void WriteMode(string mode)
    {
        File.WriteAllText(ModeFile, mode);
    }

    // The mode restricted to connected monitors (null if none of its monitors is connected).
    // Positions are shifted so the effective primary sits at 0,0.
    static ModeDef Effective(ModeDef full, DisplayState have)
    {
        var active = full.Active.Where(s => have.Connected.Contains(s.Id)).ToList();
        if (active.Count == 0) return null;
        MonitorSpec primary = active.FirstOrDefault(s => s.Id == full.Primary) ?? active[0];
        var eff = new ModeDef { Key = full.Key, Name = full.Name, Hotkey = full.Hotkey, Primary = primary.Id };
        eff.Disabled = full.Disabled.Where(have.Connected.Contains).ToList();
        foreach (var s in active)
            eff.Active.Add(new MonitorSpec { Id = s.Id, Width = s.Width, Height = s.Height, Hz = s.Hz, X = s.X - primary.X, Y = s.Y - primary.Y });
        return eff;
    }

    static bool Matches(ModeDef want, DisplayState have)
    {
        if (!want.Active.Select(s => s.Id).OrderBy(k => k).SequenceEqual(have.ActiveHz.Keys.OrderBy(k => k))) return false;
        if (want.Primary != have.Primary) return false;
        foreach (var s in want.Active)
            if (Math.Abs(have.ActiveHz[s.Id] - s.Hz) > 1) return false;
        return true;
    }

    static void Apply(ModeDef want)
    {
        int rc = Ccd.SetActive(want.Active.Select(s => s.Id).ToList());
        if (rc != 0) Log("  SetActive rc=" + rc);
        Thread.Sleep(3000);
        Gdi.SetModes(want);
        Thread.Sleep(3000);
        rc = Ccd.Persist();
        if (rc != 0) Log("  persist rc=" + rc);
    }

    public static EnforceResult Enforce(bool force, string reason)
    {
        var result = new EnforceResult();
        using (var mutex = new Mutex(false, @"Local\ScreenLayoutApply"))
        {
            try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
            try
            {
                string mode = ReadMode();
                if (string.IsNullOrEmpty(mode)) return result;
                Config cfg = Config.Load(LayoutsFile);
                ModeDef full = cfg.Mode(mode);
                if (full == null) { Log("unknown mode '" + mode + "'"); result.Detail = "Unknown mode '" + mode + "'"; return result; }

                DisplayState have = Ccd.Query();
                var missing = full.Active.Where(s => !have.Connected.Contains(s.Id)).Select(s => cfg.MonitorName(s.Id)).ToList();
                ModeDef want = Effective(full, have);
                if (want == null)
                {
                    Log(reason + ": none of mode " + mode + "'s monitors connected; leaving " + have);
                    result.Detail = string.Join(", ", missing) + " not connected";
                    return result;
                }
                if (!force && Matches(want, have)) { result.Ok = true; return result; }

                Log(reason + ": applying " + mode + " (was " + have + ")");
                Apply(want);
                DisplayState after = Ccd.Query();
                result.Applied = true;
                result.Ok = Matches(want, after);
                Log("  now " + after + (result.Ok ? " - ok" : " - MISMATCH"));

                if (!result.Ok) result.Detail = "Not fully applied - see ScreenLayout.log";
                else if (missing.Count > 0) result.Detail = string.Join(", ", missing) + " not connected";
                else result.Detail = string.Join(" + ", want.Active.Select(s => cfg.MonitorName(s.Id)));
                return result;
            }
            catch (Exception e)
            {
                Log(reason + ": error " + e.Message);
                result.Detail = e.Message;
                return result;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    // Writes layouts.ini from the connected monitors: all of them, the current layout (if only some
    // are on), and one per monitor at its current/preferred resolution; hotkeys Ctrl+Alt+1, 2, ...
    // Never overwrites.
    public static bool Init()
    {
        if (File.Exists(LayoutsFile)) { Log("init: " + LayoutsFile + " exists, not touching it"); return false; }
        List<MonitorInfo> mons = Ccd.Monitors();
        if (mons.Count == 0) { Log("init: no monitors found"); return false; }
        var order = mons.Where(mi => mi.Active).OrderBy(mi => mi.X).Concat(mons.Where(mi => !mi.Active)).ToList();
        var w = new List<string>
        {
            "; Screen layouts for ScreenLayout.exe, generated by \"init\" from the monitors connected on " + DateTime.Now.ToString("yyyy-MM-dd") + ".",
            ";",
            "; [monitors]: Short Monitor ID = display name; the toast draws them left to right in this order.",
            "; Mode sections: name, hotkey (registered by the watcher, shown in the toast), then one line per",
            "; monitor that is on:",
            ";   <Short Monitor ID> = <width>x<height>@<Hz> <x>,<y> [primary]",
            "; A monitor that is not listed in a mode is turned off. If a listed monitor isn't connected,",
            "; the rest of the layout is still applied. Restart the watcher after changing hotkeys.",
            "",
            "[monitors]",
        };
        w.AddRange(order.Select(mi => mi.Id + " = " + mi.Name));

        int hotkey = 1;
        var keys = new HashSet<string> { "monitors" };
        Action<string, string, IEnumerable<string>> mode = (key, name, lines) =>
        {
            string k = key;
            for (int n = 2; keys.Contains(k); n++) k = key + n;
            keys.Add(k);
            w.Add("");
            w.Add("[" + k + "]");
            w.Add("name = " + name);
            if (hotkey <= 9) w.Add("hotkey = Ctrl+Alt+" + hotkey++);
            w.AddRange(lines);
        };
        Func<MonitorInfo, int, int, bool, string> line = (mi, x, y, primary) =>
            mi.Id + " = " + mi.Width + "x" + mi.Height + "@" + mi.Hz + " " + x + "," + y + (primary ? " primary" : "");

        // "All": the monitors that are on where they are now, the others appended on the right.
        var active = order.Where(mi => mi.Active).ToList();
        if (order.Count > 1)
        {
            int right = active.Select(mi => mi.X + mi.Width).DefaultIfEmpty(0).Max();
            var all = active.Select(mi => line(mi, mi.X, mi.Y, mi.Primary)).ToList();
            foreach (MonitorInfo mi in order.Where(o => !o.Active))
            {
                all.Add(line(mi, right, 0, active.Count == 0 && all.Count == 0));
                right += mi.Width;
            }
            mode("all", "All monitors", all);
        }
        if (active.Count > 1 && active.Count < order.Count)
            mode("current", "Current layout", active.Select(mi => line(mi, mi.X, mi.Y, mi.Primary)));
        foreach (MonitorInfo mi in order)
        {
            string key = new string(mi.Name.Split(' ')[0].ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            mode(key.Length > 0 ? key : mi.Id.ToLowerInvariant(), order.Count > 1 ? mi.Name + " only" : mi.Name,
                new[] { line(mi, 0, 0, true) });
        }

        File.WriteAllLines(LayoutsFile, w);
        Log("init: wrote " + LayoutsFile + " for " + string.Join(", ", order.Select(mi => mi.Id + " (" + mi.Name + ")")));
        return true;
    }

    public static string Signature()
    {
        try { return ReadMode() + "|" + string.Join(",", Ccd.Query().Connected.OrderBy(s => s)); }
        catch { return ""; }
    }
}

// Non-activating card at the bottom of the primary screen: a diagram of the monitors (on = accent),
// the mode name, a status line, and the hotkeys as keycaps with the current mode highlighted.
// Painted by hand; sizes are in 96-dpi units scaled by the screen's DPI.
class Toast : Form
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(int action, int param, ref RECT rect, int winIni);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static readonly Color Bg = Color.FromArgb(30, 30, 30);
    static readonly Color Fg = Color.FromArgb(255, 255, 255);
    static readonly Color Muted = Color.FromArgb(170, 170, 170);
    static readonly Color Faint = Color.FromArgb(115, 115, 115);
    static readonly Color Accent = Color.FromArgb(96, 205, 255);
    static readonly Color OffFill = Color.FromArgb(44, 44, 44);
    static readonly Color OffEdge = Color.FromArgb(78, 78, 78);
    static readonly Color KeyFill = Color.FromArgb(46, 46, 46);
    static readonly Color Rule = Color.FromArgb(52, 52, 52);
    static readonly Color Warn = Color.FromArgb(255, 160, 140);

    readonly Config cfg;
    readonly ModeDef mode;
    readonly string modeKey;
    string detail;
    bool warn;

    readonly float s; // DPI scale
    readonly Font titleFont, detailFont, keyFont, rowFont, rowBold, labelFont;
    readonly System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
    bool fadingOut;

    // Layout, computed in Measure()
    float pad, diagH, diagLabelGap, keyW, rowH;
    Dictionary<string, RectangleF> monitorRects = new Dictionary<string, RectangleF>();

    public Toast(Config cfg, string modeKey, string detailText)
    {
        this.cfg = cfg;
        this.modeKey = modeKey;
        mode = cfg.Mode(modeKey);
        detail = detailText ?? "";

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        Opacity = 0;

        using (Graphics g = CreateGraphics()) s = g.DpiX / 96f;
        titleFont = new Font("Segoe UI Semibold", 22f * s, GraphicsUnit.Pixel);
        detailFont = new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel);
        keyFont = new Font("Segoe UI Semibold", 11.5f * s, GraphicsUnit.Pixel);
        rowFont = new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel);
        rowBold = new Font("Segoe UI Semibold", 13f * s, GraphicsUnit.Pixel);
        labelFont = new Font("Segoe UI", 11f * s, GraphicsUnit.Pixel);

        closeTimer.Tick += (o, e) => { closeTimer.Stop(); fadingOut = true; fadeTimer.Start(); };
        fadeTimer.Tick += OnFade;
        Measure();
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE
            int border = 0x00464646; // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(Handle, 34, ref border, 4); // DWMWA_BORDER_COLOR
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        fadeTimer.Start();
    }

    void OnFade(object sender, EventArgs e)
    {
        if (fadingOut)
        {
            Opacity = Math.Max(0, Opacity - 0.12);
            if (Opacity <= 0) { fadeTimer.Stop(); Close(); }
        }
        else
        {
            Opacity = Math.Min(0.97, Opacity + 0.16);
            if (Opacity >= 0.97) fadeTimer.Stop();
        }
    }

    IEnumerable<ModeDef> HotkeyModes
    {
        get { return cfg.Modes.Where(m => m.Hotkey != null); }
    }

    static string ShortName(string name)
    {
        int sp = name.IndexOf(' ');
        return sp > 0 ? name.Substring(0, sp) : name;
    }

    void Measure()
    {
        pad = 24 * s;
        diagH = 54 * s;
        diagLabelGap = 6 * s;
        rowH = 27 * s;

        // Monitor diagram: rectangles in proportion to their resolution, bottom-aligned.
        var ids = cfg.MonitorOrder.Where(id => cfg.Spec(id) != null).ToList();
        int maxH = ids.Count > 0 ? ids.Max(id => cfg.Spec(id).Height) : 1;
        float k = diagH / maxH, x = pad, diagW = 0;
        monitorRects.Clear();
        foreach (string id in ids)
        {
            MonitorSpec sp = cfg.Spec(id);
            float w = sp.Width * k, h = sp.Height * k;
            monitorRects[id] = new RectangleF(x, pad + diagH - h, w, h);
            x += w + 12 * s;
            diagW = x - 12 * s - pad;
        }

        using (Graphics g = CreateGraphics())
        {
            float titleW = g.MeasureString(Title, titleFont).Width;
            float detailW = g.MeasureString(detail, detailFont).Width;
            keyW = HotkeyModes.Select(m => g.MeasureString(m.Hotkey, keyFont).Width).DefaultIfEmpty(0).Max() + 14 * s;
            float rowW = keyW + 12 * s + HotkeyModes.Select(m => g.MeasureString(m.Name, rowBold).Width).DefaultIfEmpty(0).Max();
            float w = Math.Max(Math.Max(diagW, titleW), Math.Max(detailW, rowW));
            w = Math.Max(w, 300 * s);
            ClientSize = new Size((int)Math.Ceiling(w + 2 * pad), (int)Math.Ceiling(RowsTop + HotkeyModes.Count() * rowH + pad - 4 * s));
        }
    }

    string Title
    {
        get { return mode != null ? mode.Name : modeKey; }
    }

    float TitleTop
    {
        get { return pad + diagH + diagLabelGap + labelFont.Height + 14 * s; }
    }

    float RowsTop
    {
        get { return TitleTop + titleFont.Height + 2 * s + detailFont.Height + 30 * s; }
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var centered = new StringFormat { Alignment = StringAlignment.Center };
        var middle = new StringFormat { LineAlignment = StringAlignment.Center };
        var active = mode != null ? new HashSet<string>(mode.Active.Select(a => a.Id)) : new HashSet<string>();

        // Monitors
        foreach (var kv in monitorRects)
        {
            bool on = active.Contains(kv.Key);
            using (GraphicsPath p = Rounded(kv.Value, 4 * s))
            {
                using (var fill = new SolidBrush(on ? Accent : OffFill)) g.FillPath(fill, p);
                if (!on) using (var pen = new Pen(OffEdge, 1 * s)) g.DrawPath(pen, p);
            }
            var label = new RectangleF(kv.Value.X - 20 * s, pad + diagH + diagLabelGap, kv.Value.Width + 40 * s, labelFont.Height);
            using (var b = new SolidBrush(on ? Fg : Faint))
                g.DrawString(ShortName(cfg.MonitorName(kv.Key)), labelFont, b, label, centered);
        }

        // Title + status
        using (var b = new SolidBrush(Fg)) g.DrawString(Title, titleFont, b, pad - 2 * s, TitleTop);
        using (var b = new SolidBrush(warn ? Warn : Muted)) g.DrawString(detail, detailFont, b, pad, TitleTop + titleFont.Height + 2 * s);

        // Divider
        float ruleY = RowsTop - 15 * s;
        using (var pen = new Pen(Rule, 1 * s)) g.DrawLine(pen, pad, ruleY, ClientSize.Width - pad, ruleY);

        // Hotkeys
        float y = RowsTop;
        foreach (ModeDef m in HotkeyModes)
        {
            bool current = m.Key == modeKey;
            var key = new RectangleF(pad, y, keyW, rowH - 6 * s);
            using (GraphicsPath p = Rounded(key, 5 * s))
            {
                using (var fill = new SolidBrush(KeyFill)) g.FillPath(fill, p);
                if (current) using (var pen = new Pen(Accent, 1.2f * s)) g.DrawPath(pen, p);
            }
            using (var b = new SolidBrush(current ? Accent : Muted))
                g.DrawString(m.Hotkey, keyFont, b, key, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            using (var b = new SolidBrush(current ? Fg : Muted))
                g.DrawString(m.Name, current ? rowBold : rowFont, b, new RectangleF(key.Right + 12 * s, y, ClientSize.Width, key.Height), middle);
            y += rowH;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Reposition();
    }

    // Bottom centre of the primary screen's work area (read fresh: the primary may just have changed).
    public void Reposition()
    {
        var wa = new RECT();
        SystemParametersInfo(0x30, 0, ref wa, 0); // SPI_GETWORKAREA
        Location = new Point(wa.Left + (wa.Right - wa.Left - Width) / 2, wa.Bottom - Height - (int)(64 * s));
    }

    public void SetDetail(string text, bool isWarning)
    {
        detail = text ?? "";
        warn = isWarning;
        Measure();
        Reposition();
        Invalidate();
    }

    public void CloseAfter(int ms)
    {
        closeTimer.Stop();
        closeTimer.Interval = ms;
        closeTimer.Start();
    }

    // Shows a toast for a finished (re)apply; non-blocking, needs a message loop.
    public static void ShowResult(string modeKey, string detailText, bool isWarning, int ms)
    {
        try
        {
            var t = new Toast(Config.Load(Core.LayoutsFile), modeKey, detailText);
            t.warn = isWarning;
            t.Show();
            t.CloseAfter(ms);
        }
        catch (Exception e) { Core.Log("toast: " + e.Message); }
    }
}

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
    readonly Dictionary<string, int> attempts = new Dictionary<string, int>();
    DateTime quietUntil = DateTime.MinValue;
    string reason = "startup";

    public Watcher()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        CreateHandle();
        WTSRegisterSessionNotification(Handle, 0);
        RegisterHotkeys();
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
        hotkeyRetry.Tick += (o, e) => TryPendingHotkeys(false);
        if (pendingHotkeys.Count > 0) hotkeyRetry.Start();
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
                    if (!File.Exists(Core.LayoutsFile)) Core.Init(); // first run on this PC
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
                // Then opens the new (or existing) file for editing.
                Core.Init();
                if (File.Exists(Core.LayoutsFile)) Process.Start("notepad.exe", "\"" + Core.LayoutsFile + "\"");
                return 0;

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
