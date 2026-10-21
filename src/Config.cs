using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ScreenLayout
{
    // layouts.ini: monitor names and modes.

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
}
