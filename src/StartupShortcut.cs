using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenLayout
{
    static class StartupShortcut
    {
        // "ScreenLayout watcher" shortcut in the user's Startup folder running "<this exe> watch".
        // Rewritten every time, so it follows the exe when the folder moves. WScript.Shell is late
        // bound (no COM interop assembly with csc alone).
        public static string Save()
        {
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ScreenLayout watcher.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                try
                {
                    Type t = sc.GetType();
                    t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath });
                    t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { "watch" });
                    t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Core.BaseDir.TrimEnd('\\') });
                    t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "ScreenLayout watcher (hotkeys, keeps the layout)" });
                    t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                }
                finally
                {
                    Marshal.ReleaseComObject(sc);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
            return lnk;
        }
    }
}
