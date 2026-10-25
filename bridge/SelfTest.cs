// D6: בדיקה עצמית אוטומטית — פעם אחת לכל גרסה (ובבקשה דרך /sys/selftest): בודקת שהרכיבים קיימים ועובדים
// ורושמת את מה שנכשל ליומן (כך זה נכנס לדיווח הבאגים). שליחה אוטומטית ל-GitHub — כשהמתווך מ-A7 יוקם.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace OtzariaBridge;

public static class SelfTest
{
    public static void Init()
    {
        Server.Route("/sys/selftest", _ => Run());
        try
        {
            if (Config.Get<string>("selftest.version", "") == Program.Version) return;
            new System.Threading.Thread(() => { System.Threading.Thread.Sleep(30_000); Run(); }) { IsBackground = true }.Start();
        }
        catch { }
    }

    public record R(string name, bool ok, string error);

    public static object Run()
    {
        var res = new List<R>();
        void T(string name, Func<bool> check)
        {
            bool ok; string err = null;
            try { ok = check(); } catch (Exception ex) { ok = false; err = ex.Message; }
            res.Add(new R(name, ok, err));
        }
        T("התקנה בתיקייה הקבועה", () => File.Exists(Installer.InstalledExe));
        T("הפעלה אוטומטית (Run)", Installer.HasAutoStart);
        T("קובצי התוספים", () => Directory.Exists(Installer.PluginsDir) && Directory.GetFiles(Installer.PluginsDir, "*.otzplugin").Length >= 2);
        T("כתיבה לתיקיית ההגדרות", () => { var f = Path.Combine(Config.Dir, ".selftest"); File.WriteAllText(f, "1"); File.Delete(f); return true; });
        T("גישה לרישום (HKCU)", () => { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return k != null; });
        T("אוצריא נמצאה", () => !string.IsNullOrEmpty(Kiosk.OtzariaPath()) && File.Exists(Kiosk.OtzariaPath()));
        if (HardLock.ServiceInstalled()) T("שירות ההקשחה עונה", () => HardLock.Available);
        SelfTestMore.AddTo(T); // E1/E4
        int failed = res.Count(r => !r.ok);
        Config.Log($"selftest {Program.Version}: {res.Count - failed}/{res.Count} עברו" +
            (failed > 0 ? " — נכשלו: " + string.Join(", ", res.Where(r => !r.ok).Select(r => r.name)) : ""));
        try { Config.Set("selftest.version", Program.Version); Config.Save(); } catch { }
        return new { version = Program.Version, passed = res.Count - failed, failed, results = res };
    }
}
