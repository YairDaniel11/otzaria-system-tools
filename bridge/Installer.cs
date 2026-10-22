using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>התקנה עצמית ל-%LOCALAPPDATA%\OtzariaBridge, הפעלה אוטומטית, קיצור דרך ופריסת התוספים</summary>
public static class Installer
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValue = "OtzariaBridge";
    private const string ShortcutName = "קיוסק אוצריא.lnk";
    private static readonly string[] OldShortcutNames = { "גשר אוצריא.lnk", "תורת אסתר.lnk", "תוכנת חסימת אוצריא.lnk" }; // שמות קודמים, עד שינויי השם

    public static string InstalledExe => Path.Combine(Config.Dir, "OtzariaBridge.exe");
    public static string PluginsDir => Path.Combine(Config.Dir, "plugins");
    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    public static bool IsRunningFromInstallDir() =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>פקודת ה-Run של HKLM: ה-exe של השירות (Program Files). --portable: בלי התקנה עצמית מחדש ל-LOCALAPPDATA</summary>
    internal static string MachineRunCommand => "\"" + HardLock.ServiceExe + "\" --background --portable";

    /// <summary>האם Run של HKLM (שהשירות מגדיר בהתקנה המוגברת) מצביע על ה-exe של השירות</summary>
    internal static bool MachineRunPointsToService()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string v && v.StartsWith("\"" + HardLock.ServiceExe + "\"", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// האם ה-exe שעולה בפועל הוא עותק השירות ב-Program Files (שרק מנהל כותב). WHY (S4): במצב הזה --install מעדכן רק
    /// את LOCALAPPDATA, ולכן עדכון עצמי של הגשר לא מחליף את מה שרץ.
    /// </summary>
    internal static bool ServiceOwnsRunningExe() =>
        HardLock.ServiceInstalled() && File.Exists(HardLock.ServiceExe) && MachineRunPointsToService();

    /// <summary>האם גרסת הקובץ של ServiceExe נמוכה מגרסת התהליך הנוכחי (כלומר "הותקן" לא החליף את מה שיעלה)</summary>
    internal static bool ServiceExeOlderThanSelf()
    {
        try
        {
            var svc = UpdateApi.Parse(FileVersionInfo.GetVersionInfo(HardLock.ServiceExe).FileVersion);
            var self = UpdateApi.Parse(Program.Version);
            return svc != null && self != null && svc < self;
        }
        catch { return false; }
    }

    public static bool HasAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) != null || MachineRunPointsToService();
    }

    public static void Install(bool showMessage)
    {
        Directory.CreateDirectory(Config.Dir);

        if (!IsRunningFromInstallDir())
        {
            StopOtherInstances();
            // אחרי Kill ייתכן שהקובץ עדיין נעול לרגע (ההפעלה הבודדת לא שוחררה מיד) - מספר ניסיונות
            // עם המתנה קצרה, ואם עדיין נכשל לא מפילים את כל התהליך (בעיה אמיתית: קריסה כאן הפילה
            // את כל הגשר/השומר באמצע נעילה פעילה) - ממשיכים עם שאר ההתקנה, מנסים שוב בהפעלה הבאה
            try { CopyExeWithRetry(Environment.ProcessPath!, InstalledExe); }
            catch (Exception ex) { Config.Log("install: העתקת exe נכשלה (ימשיך עם הגרסה הקיימת): " + ex.Message); }
        }

        // כשהשירות מותקן ו-Run של HKLM מצביע על Program Files — הגשר עולה משם (קובץ שרק מנהל כותב), וערך HKCU מיותר ומוסר.
        // אחרת (בלי שירות) — כמו קודם: HKCU מ-LOCALAPPDATA
        bool machineRun = HardLock.ServiceInstalled() && File.Exists(HardLock.ServiceExe) && MachineRunPointsToService();
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (machineRun) k.DeleteValue(RunValue, false);
            else k.SetValue(RunValue, "\"" + InstalledExe + "\" --background");
        }

        foreach (string old in OldShortcutNames)
            try { string p = Path.Combine(Path.GetDirectoryName(ShortcutPath)!, old); if (File.Exists(p)) File.Delete(p); } catch { }
        try { CreateShortcut(); }
        catch (Exception ex) { Config.Log("shortcut: " + ex.Message); }

        int plugins = DeployPlugins();
        bool canInstall = plugins > 0 && OtzariaProtocolRegistered();

        // WHY (S4): כשהשירות מותקן ההפעלה היא מ-Program Files (ישן). בלי זה "עדכון הצליח" בלי להחליף: מדווחים כשל ברור
        // (קוד יציאה 3 + הודעה), ולא מעתיקים לשם מהנתיב הזה (העתקה מוגברת מחייבת אימות hash דרך התקנת רכיב ההקשחה).
        // נקודת חיבור מוצעת לנתיב עדכון אמיתי: op "update-service" ב-pipe של השירות שמקבל נתיב+SHA-256 ומריץ InstallTrust.CopyVerified.
        bool staleService = machineRun && ServiceExeOlderThanSelf();
        if (staleService)
        {
            Config.Log("install: עותק השירות ב-Program Files ישן מהגרסה המותקנת; לא עודכן. נדרשת התקנת רכיב ההקשחה מחדש כמנהל.");
            // (Program.cs מחזיר 0 אחרי Install: מוצע שם `return Environment.ExitCode;` כדי שהמתקין השקט יראה כשל)
            Environment.ExitCode = 3;
        }

        if (machineRun) Process.Start(new ProcessStartInfo(HardLock.ServiceExe, "--background --portable") { UseShellExecute = false, WorkingDirectory = HardLock.ProgDir });
        else Process.Start(new ProcessStartInfo(InstalledExe, "--background") { UseShellExecute = false, WorkingDirectory = Config.Dir });

        if (showMessage)
        {
            string msg = Lang.T(Program.AppFullName + " הותקן ויעלה אוטומטית עם כניסה ל-Windows.",
                Program.AppFullNameEn + " is installed and will start automatically when you sign in to Windows.");
            if (staleService)
                msg += Lang.T("\n\nאזהרה: הגשר מותקן כשירות (Program Files) ולא עודכן. להשלמת העדכון הריצו את קובץ ההתקנה כמנהל או התקינו מחדש את רכיב ההקשחה.",
                       "\n\nWarning: the bridge runs as a service (Program Files) and was NOT updated. Run the installer as administrator or reinstall the hardening component to finish the update.");
            if (canInstall)
                msg += Lang.T("\n\nעכשיו אוצריא תיפתח ותציע להתקין את התוספים \"סייר קבצים\" ו\"הגדרות מערכת\" — אשרו בחלון ההתקנה של אוצריא.",
                       "\n\nOtzaria will now open and offer to install the \"File Explorer\" and \"System Settings\" plugins — confirm in Otzaria's install window.");
            else if (plugins > 0)
                msg += Lang.T("\n\nבתיקייה שנפתחה נמצאים התוספים \"סייר קבצים\" ו\"הגדרות מערכת\". " +
                       "יש להתקין אותם מתוך אוצריא (התקנת תוסף מקובץ).",
                       "\n\nThe folder that opened contains the \"File Explorer\" and \"System Settings\" plugins. " +
                       "Install them from inside Otzaria (install plugin from file).");
            if (Lang.En)
                MessageBox.Show(msg, Program.AppNameEn, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(msg, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (!canInstall && plugins > 0) OpenPluginsFolder();
        }

        // גם בהתקנה שקטה (מתקין Inno): מבקשים מאוצריא להתקין את התוספים, ומחכים עד שכולם נשלחו —
        // אחרת התהליך יוצא והחוט ברקע נהרג לפני התוסף השני
        if (canInstall) InstallPluginsInOtzaria(wait: true);
    }

    /// <summary>האם אוצריא רשמה את סכמת otzaria:// (היא עושה זאת בעלייה הראשונה שלה)</summary>
    private static bool OtzariaProtocolRegistered()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\otzaria\shell\open\command")
                          ?? Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"otzaria\shell\open\command");
            return k?.GetValue("") is string v && v.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>פותח באוצריא את זרימת ההתקנה של כל תוסף (otzaria://plugin/install-local), אחד אחרי השני.
    /// אוצריא מציגה את בקשת אישור ההרשאות; המשתמש מאשר בעצמו.</summary>
    public static void InstallPluginsInOtzaria(bool wait = false)
    {
        var files = Directory.Exists(PluginsDir) ? Directory.GetFiles(PluginsDir, "*.otzplugin") : Array.Empty<string>();
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var t = new Thread(() =>
        {
            foreach (var f in files)
            {
                try
                {
                    string uri = "otzaria://plugin/install-local?path=" + Uri.EscapeDataString(Path.GetFullPath(f));
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                }
                catch (Exception ex) { Config.Log("install-local: " + ex.Message); }
                Thread.Sleep(6000);
            }
        }) { IsBackground = true };
        t.Start();
        if (wait) t.Join();
    }

    /// <summary>פורס את קובצי ה-.otzplugin המוטמעים לתיקיית plugins</summary>
    public static int DeployPlugins()
    {
        int count = 0;
        var asm = Assembly.GetExecutingAssembly();
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".otzplugin", StringComparison.OrdinalIgnoreCase)))
        {
            Directory.CreateDirectory(PluginsDir);
            string name = res.StartsWith("plugins.") ? res["plugins.".Length..] : res;
            using var s = asm.GetManifestResourceStream(res)!;
            using var f = File.Create(Path.Combine(PluginsDir, name));
            s.CopyTo(f);
            count++;
        }
        return count;
    }

    public static void OpenPluginsFolder()
    {
        if (DeployPlugins() == 0 && !Directory.Exists(PluginsDir)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + PluginsDir + "\"") { UseShellExecute = true });
    }

    private static void CreateShortcut()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(ShortcutPath);
            lnk.TargetPath = InstalledExe;
            lnk.Arguments = "--background";
            lnk.WorkingDirectory = Config.Dir;
            lnk.Description = Program.AppFullName;
            lnk.IconLocation = InstalledExe + ",0";
            lnk.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private static void StopOtherInstances()
    {
        int self = Environment.ProcessId;
        // גשר שהשירות הפעיל מוגן מהריגה ע"י המשתמש (DACL) כל עוד הנעילה נאכפת. כשהיא לא נאכפת הוא משחרר את ההגנה בתוך ~5 שניות
        // (HardLock.SyncOwnDacl) — לכן ניסיונות חוזרים, ולא כשל מיידי. נעילה נאכפת = ההתקנה/הסרה לא אמורות לקרות ממילא
        for (int attempt = 0; attempt < 4; attempt++)
        {
            bool denied = false;
            foreach (var p in Process.GetProcessesByName("OtzariaBridge"))
            {
                if (p.Id == self) continue;
                try { p.Kill(); p.WaitForExit(3000); }
                catch (System.ComponentModel.Win32Exception) { denied = true; }
                catch { }
            }
            if (!denied) return;
            // גשר שהשירות הפעיל מוגן מהריגה: בקשה מאומתת לשירות (הוא עצמו הורג), מותרת רק כשהנעילה לא נאכפת
            HardLock.Call(new System.Text.Json.Nodes.JsonObject { ["op"] = "stop-bridges" });
            Thread.Sleep(2000);
        }
    }

    /// <summary>עד 5 ניסיונות עם המתנה - תהליך שזה עתה נהרג עשוי להחזיק עדיין את ה-handle של ה-exe לרגע</summary>
    private static void CopyExeWithRetry(string src, string dst)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(src, dst, overwrite: true); return; }
            catch (IOException) when (i < 4) { Thread.Sleep(400); }
        }
    }

    /// <summary>מחזיר הודעת שגיאה, או null בהצלחה</summary>
    public static string Uninstall(string password)
    {
        // רכיב ההקשחה (אם מותקן) מוסר קודם, עם אותה סיסמה
        string herr = HardLock.UninstallWithPassword(password);
        if (herr != null) return herr;
        string err = Kiosk.ReleaseForUninstall(password);
        if (err != null) return err;

        StopOtherInstances();
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) k?.DeleteValue(RunValue, false);
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }

        if (IsRunningFromInstallDir())
        {
            // אי אפשר למחוק קובץ שרץ; מוחקים אחרי היציאה
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Config.Dir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        else
        {
            try { Directory.Delete(Config.Dir, true); } catch (Exception ex) { return "התיקייה לא נמחקה: " + ex.Message; }
        }
        return null;
    }
}
