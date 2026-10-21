using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScreenLayout
{
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
}
