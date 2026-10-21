using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScreenLayout
{
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
}
