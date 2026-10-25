// E1/E4: בדיקות עצמיות נוספות שרצות אוטומטית (עם SelfTest, פעם לכל גרסה):
//  E1 — פותח דיאלוג "פתיחה" אמיתי בתהליך-בן ובודק ש-DialogGuard מזהה אותו (AutomationId 1000/1001).
//  E4 — תהליך-בן קורס בכוונה ובודקים שהקריסה נרשמה לדיווח, ושיש מנגנון חזרה (הפעלה אוטומטית / שירות).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;

namespace OtzariaBridge;

public static class SelfTestMore
{
    /// <summary>נקרא מ-Program.Main: מצבי תהליך-בן של הבדיקה. מחזיר null אם זה לא מצב בדיקה</summary>
    public static int? Child(string[] args)
    {
        if (Array.IndexOf(args, "--selftest-dialog") >= 0)
        {
            using var d = new System.Windows.Forms.OpenFileDialog { Title = "selftest" };
            d.ShowDialog();
            return 0;
        }
        if (Array.IndexOf(args, "--selftest-crash") >= 0)
        {
            Outbox.Init();
            throw new InvalidOperationException("selftest-crash: קריסה מכוונת של הבדיקה העצמית");
        }
        return null;
    }

    public static void AddTo(Action<string, Func<bool>> T)
    {
        T("E1 זיהוי דיאלוג פתיחה/שמירה", DialogDetected);
        T("E4 קריסה נרשמת לדיווח", CrashReported);
        T("E4 חזרה אחרי קריסה (הפעלה אוטומטית/שירות)", () => Installer.HasAutoStart() || HardLock.ServiceInstalled());
    }

    static bool DialogDetected()
    {
        using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--selftest-dialog") { UseShellExecute = false });
        try
        {
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(250);
                IntPtr found = IntPtr.Zero;
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != p.Id || !IsWindowVisible(h)) return true;
                    var sb = new StringBuilder(64); GetClassName(h, sb, 64);
                    if (sb.ToString() == "#32770") { found = h; return false; }
                    return true;
                }, IntPtr.Zero);
                if (found == IntPtr.Zero) continue;
                Thread.Sleep(500); // שהפקדים ייטענו
                IUIAutomation ui; try { ui = new CUIAutomation8(); } catch { ui = new CUIAutomation(); }
                return DialogGuard.LooksLikeFileDialog(ui, found);
            }
            throw new Exception("הדיאלוג לא הופיע");
        }
        finally { try { p.Kill(); } catch { } }
    }

    static bool CrashReported()
    {
        string dir = Path.Combine(Config.Dir, "outbox");
        var before = Directory.Exists(dir) ? Directory.GetFiles(dir, "*-crash.txt").Length : 0;
        using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--selftest-crash") { UseShellExecute = false, CreateNoWindow = true });
        p.WaitForExit(15_000);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*-crash.txt") : Array.Empty<string>();
        var mine = files.Where(f => File.ReadAllText(f).Contains("selftest-crash")).ToList();
        foreach (var f in mine) try { File.Delete(f); } catch { } // לא לשלוח את הקריסה המכוונת כתקלה
        return files.Length > before || mine.Count > 0;
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
}
