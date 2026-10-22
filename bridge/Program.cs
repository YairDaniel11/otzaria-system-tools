using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

public static class Program
{
    public const string Version = "3.6.1";
    public static int Port { get; set; }

    // שם המוצר (מהגרסה האישית/פורק הזה): "קיוסק אוצריא", בלי זיקה לשם הגמ"ח הקודם
    public const string AppName = "קיוסק אוצריא";
    public const string AppNameEn = "Otzaria Kiosk";
    public const string AppFullName = AppName + " - תוכנה לחסימת המחשב ללימוד";
    public const string AppFullNameEn = AppNameEn + " - software for locking the computer for study";

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    /// <summary>
    /// יציאה כשה-mutex תפוס. גשר שהשירות הפעיל לא יוצא: השירות כבר קבע (לפי נתיב) שאין גשר אמיתי בסשן, ולכן mutex תפוס הוא
    /// תהליך זר שתפס את השם מראש (mutex squat) ולא מופע לגיטימי. WHY: אחרת כל תהליך משתמש היה מונע מהגשר לעלות בנעילה.
    /// מופע כפול של גשר שהמשתמש הפעיל — עדיין יוצא כמו קודם
    /// </summary>
    internal static bool ShouldExitOnMutex(bool first, bool fromService) => !first && !fromService;

    [STAThread]
    public static int Main(string[] args)
    {
        string Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string name) => Array.IndexOf(args, name) >= 0;

        try
        {
            // רכיב ההקשחה (שירות Windows, SYSTEM) — בעיה #1
            if (Has("--service"))
            {
                System.ServiceProcess.ServiceBase.Run(new HardLockService());
                return 0;
            }
            if (Has("--install-service")) return HardLock.InstallService();

            Lang.FromArgs(args);

            // תהליך עזר שהשירות מפעיל בסשן של המשתמש כדי להציג את חלון Windows Hello (סשן 0 של השירות לא יכול)
            if (Has("--hello-helper"))
            {
                int i = Array.IndexOf(args, "--hello-helper");
                string nonce = i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
                bool ok = TwoFactor.Verify(Lang.T("אימות פעולה ברכיב ההקשחה של " + AppName, "Verify an action in the " + AppNameEn + " hardened lock"));
                HardLock.Call(new JsonObject { ["op"] = "hello-done", ["nonce"] = nonce, ["ok"] = ok });
                return ok ? 0 : 2;
            }

            if (Has("--unlock"))
            {
                AttachConsole(-1);
                bool ok = Kiosk.EmergencyUnlock(Arg("--unlock") ?? "");
                ok = HardLock.EmergencyDisable(Arg("--unlock") ?? "") || ok;
                Console.WriteLine(ok ? Lang.T("הנעילה בוטלה.", "Lock mode turned off.") : Lang.T("קוד שחזור שגוי.", "Wrong recovery code."));
                return ok ? 0 : 2;
            }
            if (Has("--uninstall"))
            {
                AttachConsole(-1);
                string err = Installer.Uninstall(Arg("--password"));
                Console.WriteLine(err ?? Lang.T("הגשר הוסר.", "The bridge was removed."));
                return err == null ? 0 : 2;
            }
            if (Has("--install") || (!Installer.IsRunningFromInstallDir() && !Has("--portable")))
            {
                Installer.Install(showMessage: !Has("--quiet"));
                return 0;
            }

            // לפני הכול: אם אין נעילה פעילה — שולחן העבודה חייב לעלות (בעיה #8)
            Kiosk.BootGuard();

            InstallTrust.CaptureSelf(); // גיבוב ה-exe הרץ נתפס עכשיו (לאימות התקנה מוגברת מאוחרת), לפני שמשתמש יספיק להחליף את הקובץ

            using var mutex = new Mutex(true, @"Local\OtzariaBridge", out bool first);
            if (ShouldExitOnMutex(first, Has("--from-service"))) return 0;

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Tray.Init();
            Outbox.Init();
            Reports.Init();
            Port = Server.Start();
            Kiosk.RestoreOnStartup();
            HardLock.StartSync();
            Kiosk.StartScheduler();
            // אכיפת FxPolicy/Kiosk.AllowedExtensions על דיאלוגי פתיחה/שמירה מקוריים של Windows בכל תהליך
            // (בעיה #53/וורד: "קובץ ← פתיחה ← עיון" בתוכנה חיצונית מאושרת עוקף את הסייר הפנימי). ר' DialogGuard.cs
            DialogGuard.Start();
            Tray.Run();
            Server.Stop();
            return 0;
        }
        catch (Exception ex)
        {
            Config.Log("fatal: " + ex);
            Outbox.Add("fatal", ex.ToString());
            // קריסה בעלייה לא משאירה מסך שחור: אם אין נעילה פעילה, מחזירים את שולחן העבודה
            try { if (!Kiosk.IsActive) Kiosk.BootGuard(); } catch { }
            return 1;
        }
    }
}

/// <summary>קובץ הגדרות ב-%LOCALAPPDATA%\OtzariaBridge\config.json</summary>
public static class Config
{
    private static readonly object Gate = new();
    private static JsonObject _data;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridge");
    public static string FilePath => Path.Combine(Dir, "config.json");

    private static JsonObject Data
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                if (File.Exists(FilePath))
                    _data = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            }
            catch { }
            return _data ??= new JsonObject();
        }
    }

    public static T Get<T>(string key, T fallback)
    {
        lock (Gate)
        {
            try
            {
                var n = Data[key];
                return n == null ? fallback : n.GetValue<T>();
            }
            catch { return fallback; }
        }
    }

    public static bool HasList(string key)
    {
        lock (Gate) { return Data[key] is JsonArray; }
    }

    public static string[] GetList(string key)
    {
        lock (Gate)
        {
            if (Data[key] is JsonArray a)
                return a.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
            return Array.Empty<string>();
        }
    }

    public static void Set(string key, JsonNode value)
    {
        lock (Gate) { Data[key] = value; }
    }

    public static void Remove(string key)
    {
        lock (Gate) { Data.Remove(key); }
    }

    public static void Reload()
    {
        lock (Gate) { _data = null; }
    }

    // TypeInfoResolver מפורש הכרחי (ר' הערה זהה ב-HardLock.cs.Save) - בלעדיו JsonNode.ToJsonString(options)
    // עלול לזרוק "JsonSerializerOptions instance must specify a TypeInfoResolver" בתהליך השירות
    private static readonly JsonSerializerOptions SaveOptions =
        new() { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    public static void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Data.ToJsonString(SaveOptions));
            File.Move(tmp, FilePath, true);
        }
    }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "bridge.log"), DateTime.Now.ToString("s") + " " + line + Environment.NewLine);
        }
        catch { }
    }

    public static string Run(string file, string arguments, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        if (p == null) return "";
        // קוראים את שני הזרמים במקביל (async). אחרת, אם הילד ממלא את ה-buffer של stderr (שלא נקרא),
        // הוא נחסם, לא סוגר את stdout, ו-ReadToEnd נתקע לנצח — לפני ש-WaitForExit בכלל רץ, כך שה-timeout
        // לא עוזר. בנוסף: הריגה בפועל בטיים-אאוט, לא רק Dispose.
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        try { Task.WaitAll(new Task[] { outTask, errTask }, 3000); } catch { }
        return outTask.Status == TaskStatus.RanToCompletion ? (outTask.Result ?? "") : "";
    }
}
