using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ScreenLayout
{
    // Runtime files next to the exe, and applying the chosen mode.

    class EnforceResult
    {
        public bool Applied, Ok;
        public string Detail;
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

        public static string Signature()
        {
            try { return ReadMode() + "|" + string.Join(",", Ccd.Query().Connected.OrderBy(s => s)); }
            catch { return ""; }
        }
    }
}
