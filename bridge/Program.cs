using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

public static class Program
{
    // הגרסה נגזרת מ-<Version> שב-csproj (מקור אחד) — קבוע ידני נשכח ב-3.8.5 והגשר דיווח 3.8.0
    public static readonly string Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static int Port { get; set; }

    // שם המוצר (13.10.2026): "אחת שאלתי". קודם: "תוכנת חסימת אוצריא". הוחלף מ"תורת אסתר" לשם שמתאר בעברית פשוטה את מה שהתוכנה עושה, כי "קיוסק" ואפילו
    // "תורת אסתר" לבדו לא היו ברורים למשתמשים בלי רקע טכני. "תורת אסתר" נשאר כשם ההנצחה (הגמ"ח).
    public const string AppName = "אחת שאלתי";
    public const string AppNameEn = "Achat Sha'alti";
    public const string GemachName = "גמח תורת אסתר";
    public const string GemachNameEn = "Torat Esther Gemach";
    // סלוגן מדויק לפי בעלת הריפו (14.10.2026)
    public const string Slogan = "תוכנת חסימת מחשב לתוכנות תורניות בלבד";
    public const string SloganEn = "Computer lock for Torah software only";
    public const string Dedication = "לע\"נ אסתר גיטה דינר ע\"ה";
    public const string AppFullName = AppName + " - " + Slogan;
    public const string AppFullNameEn = AppNameEn + " — " + SloganEn;

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
        // בקשות הגשר חוסמות חוטים (powercfg/WMI/PowerShell) — בלי הגדלת המינימום ה-ThreadPool מתנפח לאט ובקשת /health נתקעת מאחוריהן, והתוסף חושב שהגשר נפל
        System.Threading.ThreadPool.SetMinThreads(64, 64);
        string Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string name) => Array.IndexOf(args, name) >= 0;
        var stc = SelfTestMore.Child(args); if (stc != null) return stc.Value; // E1/E4

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
                // S1 ממצא 4: קוד השחזור מ-stdin/הקלדה מוסתרת (שורת פקודה נחשפת לכל משתמש). "--unlock CODE" נשאר לתאימות, עם אזהרה
                string code = CliSecret.Resolve(args, "--unlock",
                    Lang.T("קוד שחזור: ", "Recovery code: "),
                    Lang.T("אזהרה: קוד בשורת הפקודה נחשף לתהליכים אחרים. העדיפו --unlock (בלי ערך) או --unlock - עם stdin.",
                           "Warning: a code on the command line is visible to other processes. Prefer --unlock with no value, or --unlock - with stdin."));
                bool ok = Kiosk.EmergencyUnlock(code);
                ok = HardLock.EmergencyDisable(code) || ok;
                Console.WriteLine(ok ? Lang.T("הנעילה בוטלה.", "Lock mode turned off.") : Lang.T("קוד שחזור שגוי.", "Wrong recovery code."));
                return ok ? 0 : 2;
            }
            if (Has("--uninstall"))
            {
                AttachConsole(-1);
                // S1 ממצא 4: כמו --unlock: סיסמה מ-stdin/הקלדה מוסתרת; "--password X" נשאר לתאימות עם אזהרה
                string pwd = CliSecret.Resolve(args, "--password",
                    Lang.T("סיסמה: ", "Password: "),
                    Lang.T("אזהרה: סיסמה בשורת הפקודה נחשפת לתהליכים אחרים. העדיפו --password (בלי ערך) או --password - עם stdin.",
                           "Warning: a password on the command line is visible to other processes. Prefer --password with no value, or --password - with stdin."));
                string err = Installer.Uninstall(pwd);
                Console.WriteLine(err ?? Lang.T("הגשר הוסר.", "The bridge was removed."));
                return err == null ? 0 : 2;
            }
            if (Has("--install") || (!Installer.IsRunningFromInstallDir() && !Has("--portable")))
            {
                Installer.Install(showMessage: !Has("--quiet"));
                // WHY (T1): Install מסמן Environment.ExitCode=3 (שירות ישן שלא הוחלף); בלי החזרה כאן המתקין השקט ראה תמיד הצלחה
                return Environment.ExitCode;
            }

            // לפני הכול: אם אין נעילה פעילה — שולחן העבודה חייב לעלות (בעיה #8)
            Kiosk.BootGuard();

            InstallTrust.CaptureSelf(); // גיבוב ה-exe הרץ נתפס עכשיו (לאימות התקנה מוגברת מאוחרת), לפני שמשתמש יספיק להחליף את הקובץ

            // "פתח באמצעות ← צופה PDF+OCR": הקובץ נכנס לתור ואוצריא נפתחת בתוסף; אם הגשר כבר רץ — יוצאים, אחרת ממשיכים לעלות כרגיל
            if (Has("--open-pdf")) PdfAssoc.Enqueue(Arg("--open-pdf"));

            using var mutex = new Mutex(true, @"Local\OtzariaBridge", out bool first);
            if (ShouldExitOnMutex(first, Has("--from-service"))) return 0;
            Installer.CleanupOldExe();

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Tray.Init();
            Screenshot.Init(); // #60
            Outbox.Init();
            GhRelay.Init(); GhFeedback.Register();
            Watermark.Init();
            Reports.Init();
            Port = Server.Start();
            Kiosk.RestoreOnStartup();
            HardLock.StartSync();
            Kiosk.StartScheduler();
            UpdateRollback.StartHealthReporter(); // X3 (נקודת חיבור): סימן תקינות אחרי עדכון
            HandleLock.StartSync();               // X3 (נקודת חיבור): handle ללא FILE_SHARE_DELETE בזמן נעילה
            Zmanim.StartAlerts(); // #33
            LockScreenImage.Start(); // משוב 15.10
            Saver.Start(); // D2+D3
            SelfTest.Init(); // D6
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

    /// <summary>true כש-config.json וגם הגיבוי לא נקראו (הגדרות ריקות בפועל). מי שנשען על "ריק = לא נעול" צריך לדעת (ר' StatusNode)</summary>
    public static volatile bool CorruptNoBackup;

    private static JsonObject Data
    {
        get
        {
            if (_data != null) return _data;
            // WHY (qa-final-a #11): קובץ חתוך/ריק (הפסקת חשמל) לא מפורש יותר בשקט כ"הגדרות ריקות" (kiosk.active=false, בלי סיסמה). קודם גיבוי (.bak),
            // ואם גם הוא פגום: דגל CorruptNoBackup, והעותק הפגום נשמר ל-.corrupt לפני שה-Save הבא דורס אותו
            _data = StateStore.Read(FilePath, out var st, out bool restored);
            if (restored) Log("config: config.json פגום/חסר; שוחזר מ-config.json.bak");
            if (st == StateLoad.Corrupt)
            {
                CorruptNoBackup = true;
                try { File.Copy(FilePath, FilePath + ".corrupt", true); } catch { }
                Log("config: config.json וגם הגיבוי פגומים — מתחילים מהגדרות ריקות, העותק הפגום נשמר ב-config.json.corrupt");
            }
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

    public static bool Has(string key)
    {
        lock (Gate) { return Data[key] != null; }
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

    public static string[] Keys()
    {
        lock (Gate) { return Data.Select(kv => kv.Key).ToArray(); }
    }
    public static JsonNode Raw(string key)
    {
        lock (Gate) { return Data[key]?.DeepClone(); }
    }

    public static void Remove(string key)
    {
        lock (Gate) { Data.Remove(key); }
    }

    /// <summary>עותק עמוק של ההגדרות הנוכחיות (לחתימה/אימות ConfigSeal בלי לחשוף את האובייקט המשותף)</summary>
    public static JsonObject Snapshot()
    {
        lock (Gate) { return (JsonObject)Data.DeepClone(); }
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
            // אטומי: זמני + Flush(true) + גיבוי של התקין האחרון + החלפה (כמו hardlock.json)
            StateStore.WriteAtomic(FilePath, Data.ToJsonString(SaveOptions));
            CorruptNoBackup = false;
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
