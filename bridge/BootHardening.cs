using System.Management;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// הקשחת אתחול ומערכת (שכבת OS לפני/סביב האתחול), נאכפת מהשירות (SYSTEM) בזמן נעילה פעילה:
///   1. רישום השירות ב-SafeBoot\Minimal ו-Network, כדי שהנעילה תחזיק גם ב-Safe Mode (אחרת Safe Mode עוקף אותה).
///   2. reagentc /disable: ללא WinRE אי אפשר להגיע ל-Command Prompt/איפוס מתפריט ההתאוששות.
///   3. bcdedit: recoveryenabled=No (אין כניסה אוטומטית ל-WinRE אחרי כשלי אתחול), bootstatuspolicy=IgnoreAllFailures,
///      bootmenupolicy=Standard (מבטל את תפריט F8 הישן שמאפשר Safe Mode).
/// העיקרון (כמו WinRecovery אצל KioskLock): מחזירים רק מה שאנחנו שינינו. לפני כל שינוי נרשם הערך המקורי ב-boothardening.json
/// (ב-DataDir, נכתב רק מהשירות), ובשחרור הנעילה כל רשומה מוחזרת ואז נמחקת. שינוי שכבר היה בערכו הרצוי לפני כן לא נרשם ולא מוחזר.
/// כל כשל מדווח (incomplete) ולא נבלע; רשומה שלא הוחזרה נשארת וממשיכים לנסות.
/// מעבר לאכיפה: דיווח מצב (BitLocker/TPM/Secure Boot) ב-/bios/status, כדי שהמנהל יראה בבירור מה חסר. מה שלא ניתן לאכוף בתוכנה
/// (BitLocker עם PIN, סיסמת BIOS ידנית) מתועד ב-docs\BOOT_HARDENING.md.
/// הכל עובר דרך IBootHost, כדי שהלוגיקה תהיה ניתנת לבדיקה בלי bcdedit/reagentc/רישום אמיתיים (ר' tests\BootHardeningTests.cs).
/// </summary>
public static class BootHardening
{
    /// <summary>שם המפתח תחת SafeBoot: השם הקצר של השירות</summary>
    public static string SafeBootName => HardLock.ServiceName;

    // ---------- הממשק לעולם החיצון ----------

    public interface IBootHost
    {
        /// <summary>מריץ כלי (bcdedit/reagentc) ומחזיר את הפלט המשולב; חריגה אם נכשל להפעלה</summary>
        string Run(string file, string args);
        bool SafeBootExists(string set, string name);
        /// <summary>יוצר HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\{set}\{name} עם ערך ברירת מחדל "Service"; false בכשל</summary>
        bool SafeBootCreate(string set, string name);
        bool SafeBootDelete(string set, string name);
        string ReadState();
        void WriteState(string json);
        void Log(string line);
    }

    public sealed class Step
    {
        public string Id { get; set; }
        public bool Ok { get; set; }
        public bool Changed { get; set; }
        public string Message { get; set; }
    }

    public sealed class Result
    {
        public List<Step> Steps { get; } = new();
        public bool Incomplete => Steps.Any(s => !s.Ok);
    }

    // ---------- ערכי היעד של bcdedit ----------

    internal static readonly (string name, string desired)[] BcdTargets =
    {
        ("recoveryenabled", "No"),
        ("bootstatuspolicy", "IgnoreAllFailures"),
        ("bootmenupolicy", "Standard"),
    };

    /// <summary>ברירות מחדל של Windows לשחזור כשרשומת המקור אבדה (בשחרור מלא בלבד). bootmenupolicy לא כאן: Standard היא ברירת המחדל</summary>
    internal static readonly Dictionary<string, string> LostStateDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["recoveryenabled"] = "Yes",
        ["bootstatuspolicy"] = "DisplayAllFailures",
    };

    // ---------- פרסור (טהור) ----------

    /// <summary>ערך שדה מפלט `bcdedit /enum`: שורה "שם   ערך". null אם השדה לא מופיע (= ברירת מחדל של המערכת)</summary>
    internal static string ParseBcdValue(string text, string name)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, @"^[ \t]*" + Regex.Escape(name) + @"[ \t]+(\S[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>האם פלט `bcdedit /enum {current}` נראה כמו רשומת אתחול אמיתית (ולא הודעת שגיאה/ריק)</summary>
    internal static bool LooksLikeBcdEntry(string text) =>
        !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"^[ \t]*identifier[ \t]+\S", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// מצב WinRE מ-`reagentc /info`: true/false/null (לא ידוע). הטקסט מתורגם בחלק מה-Windows, לכן לא מסתמכים על "Enabled":
    /// כשהוא פעיל, שורת המיקום מכילה נתיב GLOBALROOT; כשכבוי המיקום ריק. פלט בלי "Windows RE" ובלי GLOBALROOT = לא ידוע.
    /// </summary>
    internal static bool? ParseReagentcEnabled(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.IndexOf("GLOBALROOT", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (text.IndexOf("Windows RE", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("WinRE", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return null;
    }

    // ---------- אכיפה / שחרור (idempotent) ----------

    /// <summary>
    /// active=true: מחיל הקשחה ורושם מקור. active=false: מחזיר רק רשומות קיימות. בטוח להרצה חוזרת.
    /// </summary>
    public static Result Enforce(IBootHost host, bool active, bool full = false)
    {
        // full (רק בהסרה מלאה): משחזר גם כשקובץ המצב אבד / הדגלים לא נשמרו (WHY, באג VM: WinRE ו-recoveryenabled=No נשארו אחרי הסרה)
        var res = new Result();
        JsonObject st = LoadState(host);
        bool dirty = false;

        // --- Safe Mode ---
        foreach (string set in new[] { "Minimal", "Network" })
        {
            string key = "safeboot" + set;
            if (active)
            {
                if (host.SafeBootExists(set, SafeBootName))
                    res.Steps.Add(new Step { Id = key, Ok = true, Message = "השירות כבר רשום ב-SafeBoot\\" + set });
                else
                {
                    // רושמים לפני הפעולה: קריסה באמצע משאירה רשומה שהחזרתה חסרת-נזק (מחיקה של מפתח שלא קיים)
                    st[key] = true; Save(host, st);
                    bool ok = host.SafeBootCreate(set, SafeBootName) && host.SafeBootExists(set, SafeBootName);
                    res.Steps.Add(new Step { Id = key, Ok = ok, Changed = ok, Message = ok ? "השירות נרשם ב-SafeBoot\\" + set : "רישום ב-SafeBoot\\" + set + " נכשל" });
                    if (!ok) { st.Remove(key); dirty = true; }
                }
            }
            else if (full || st[key]?.GetValue<bool>() == true)
            {
                bool ok = !host.SafeBootExists(set, SafeBootName) || (host.SafeBootDelete(set, SafeBootName) && !host.SafeBootExists(set, SafeBootName));
                res.Steps.Add(new Step { Id = key, Ok = ok, Changed = ok, Message = ok ? "הרישום ב-SafeBoot\\" + set + " הוסר" : "הסרת הרישום מ-SafeBoot\\" + set + " נכשלה (יחזור לנסות)" });
                if (ok) { st.Remove(key); dirty = true; }
            }
        }

        // --- WinRE ---
        try
        {
            if (active)
            {
                bool? en = ParseReagentcEnabled(host.Run("reagentc.exe", "/info"));
                if (en == true)
                {
                    st["reagentcDisabledByUs"] = true; Save(host, st);
                    host.Run("reagentc.exe", "/disable");
                    bool? after = ParseReagentcEnabled(host.Run("reagentc.exe", "/info"));
                    bool ok = after == false;
                    res.Steps.Add(new Step { Id = "reagentc", Ok = ok, Changed = ok, Message = ok ? "WinRE כובה" : "reagentc /disable לא אומת" });
                    if (!ok) { st.Remove("reagentcDisabledByUs"); dirty = true; }
                }
                else if (en == false) res.Steps.Add(new Step { Id = "reagentc", Ok = true, Message = "WinRE כבר כבוי" });
                else res.Steps.Add(new Step { Id = "reagentc", Ok = false, Message = "לא ניתן לקרוא את מצב WinRE (reagentc /info)" });
            }
            else if (st["reagentcDisabledByUs"]?.GetValue<bool>() == true || (full && ParseReagentcEnabled(host.Run("reagentc.exe", "/info")) != true))
            {
                host.Run("reagentc.exe", "/enable");
                bool ok = ParseReagentcEnabled(host.Run("reagentc.exe", "/info")) == true;
                res.Steps.Add(new Step { Id = "reagentc", Ok = ok, Changed = ok, Message = ok ? "WinRE הופעל מחדש" : "הפעלה מחדש של WinRE לא אומתה (יחזור לנסות)" });
                if (ok) { st.Remove("reagentcDisabledByUs"); dirty = true; }
            }
        }
        catch (Exception ex) { res.Steps.Add(new Step { Id = "reagentc", Ok = false, Message = "reagentc: " + ex.Message }); }

        // --- bcdedit ---
        EnforceBcd(host, active, st, res, ref dirty, full);

        // בכל מצב: שומרים את סיכום המצב, כדי ש-/bios/status יראה "הקשחה לא הושלמה" כשצריך
        st["incomplete"] = new JsonArray(res.Steps.Where(s => !s.Ok).Select(s => (JsonNode)JsonValue.Create(s.Id + ": " + s.Message)).ToArray());
        st["active"] = active;
        st["updated"] = DateTime.Now.ToString("s");
        Save(host, st);
        foreach (var s in res.Steps.Where(s => s.Changed || !s.Ok)) host.Log("boot-hardening: " + s.Id + (s.Ok ? " OK " : " נכשל ") + s.Message);
        return res;
    }

    private static void EnforceBcd(IBootHost host, bool active, JsonObject st, Result res, ref bool dirty, bool full = false)
    {
        var recs = st["bcd"] as JsonObject ?? new JsonObject();
        st["bcd"] = recs;
        if (!active && recs.Count == 0 && !full) return;

        string enumText;
        try { enumText = host.Run("bcdedit.exe", "/enum {current}"); }
        catch (Exception ex) { res.Steps.Add(new Step { Id = "bcd", Ok = false, Message = "bcdedit: " + ex.Message }); return; }
        if (!LooksLikeBcdEntry(enumText))
        {
            // לא מנחשים: בלי קריאה תקינה אי אפשר לדעת מה המקור, ולכן לא נוגעים (ובשחרור: הרשומות נשארות)
            res.Steps.Add(new Step { Id = "bcd", Ok = false, Message = "קריאת bcdedit /enum {current} נכשלה או לא זוהתה" });
            return;
        }

        foreach (var (name, desired) in BcdTargets)
        {
            string id = "bcd." + name;
            string cur = ParseBcdValue(enumText, name);
            bool has = recs[name] is JsonObject;
            if (active)
            {
                if (string.Equals(cur, desired, StringComparison.OrdinalIgnoreCase))
                {
                    res.Steps.Add(new Step { Id = id, Ok = true, Message = name + " כבר " + desired });
                    continue;
                }
                if (!has) { recs[name] = new JsonObject { ["orig"] = cur }; Save(host, st); }
                host.Run("bcdedit.exe", "/set {current} " + name + " " + desired);
                string now = ParseBcdValue(host.Run("bcdedit.exe", "/enum {current}"), name);
                bool ok = string.Equals(now, desired, StringComparison.OrdinalIgnoreCase);
                res.Steps.Add(new Step { Id = id, Ok = ok, Changed = ok, Message = ok ? name + " הוגדר ל-" + desired + " (מקור: " + (cur ?? "ברירת מחדל") + ")" : name + " לא הוגדר (עכשיו: " + (now ?? "ברירת מחדל") + ")" });
                if (!ok && !has) { recs.Remove(name); dirty = true; }
            }
            else if (!has && full && LostStateDefaults.TryGetValue(name, out string dflt) && string.Equals(cur, desired, StringComparison.OrdinalIgnoreCase))
            {
                // אין רשומת מקור (המצב אבד): חוזרים לברירת המחדל של Windows (recoveryenabled=Yes, bootstatuspolicy=DisplayAllFailures; bootmenupolicy=Standard כבר ברירת המחדל)
                host.Run("bcdedit.exe", "/set {current} " + name + " " + dflt);
                string now = ParseBcdValue(host.Run("bcdedit.exe", "/enum {current}"), name);
                bool ok = string.Equals(now, dflt, StringComparison.OrdinalIgnoreCase);
                res.Steps.Add(new Step { Id = id, Ok = ok, Changed = ok, Message = ok ? name + " הוחזר ל-" + dflt : name + " לא הוחזר (יחזור לנסות)" });
            }
            else if (has)
            {
                string orig = (recs[name] as JsonObject)["orig"]?.GetValue<string>();
                if (orig == null) host.Run("bcdedit.exe", "/deletevalue {current} " + name);
                else host.Run("bcdedit.exe", "/set {current} " + name + " " + orig);
                string now = ParseBcdValue(host.Run("bcdedit.exe", "/enum {current}"), name);
                bool ok = string.Equals(now, orig, StringComparison.OrdinalIgnoreCase); // orig==null: אחרי deletevalue השדה נעלם מהפלט
                res.Steps.Add(new Step { Id = id, Ok = ok, Changed = ok, Message = ok ? name + " הוחזר" : name + " לא הוחזר (יחזור לנסות)" });
                if (ok) { recs.Remove(name); dirty = true; }
            }
        }
    }

    private static JsonObject LoadState(IBootHost host)
    {
        try
        {
            string s = host.ReadState();
            if (!string.IsNullOrWhiteSpace(s) && JsonNode.Parse(s) is JsonObject o) return o;
        }
        catch (Exception ex) { host.Log("boot-hardening: קובץ מצב פגום, מתחילים נקי: " + ex.Message); }
        return new JsonObject();
    }

    private static void Save(IBootHost host, JsonObject st)
    {
        try { host.WriteState(st.ToJsonString()); }
        catch (Exception ex) { host.Log("boot-hardening: שמירת מצב נכשלה: " + ex.Message); }
    }

    // ---------- נקודת הכניסה מהשירות (HardLock.Tick) ----------

    private static bool? _lastActive;
    private static Result _lastResult;
    /// <summary>תוצאת ההרצה המלאה האחרונה (להסרה: האם השחרור הושלם)</summary>
    internal static bool LastFullReleaseComplete => _lastResult != null && !_lastResult.Incomplete;
    private static int _ticks;

    /// <summary>
    /// נקראת בכל Tick (5 שניות) מהשירות. מריצה אכיפה רק במעבר active, או אחת לכ-5 דקות כאימות חוזר
    /// (מישהו שינה bcdedit/מחק מפתח SafeBoot). לא זורקת חריגה: כשל ברישום ביומן בלבד.
    /// </summary>
    public static void Enforce(bool active, bool full = false)
    {
        try
        {
            var host = new RealHost();
            var r = EnforceLatched(host, active, full);
            if (r == null) return;
            if (r.Incomplete) host.Log("boot-hardening: הקשחה לא הושלמה: " + string.Join("; ", r.Steps.Where(s => !s.Ok).Select(s => s.Id)));
            WritePosture(host);
        }
        catch (Exception ex) { Config.Log("boot-hardening: " + ex.Message); }
    }

    // WHY (qa-final-a #7): Tick (כל 5ש') ו-SelfUninstall רצו במקביל על אותם שדות סטטיים ואותו קובץ מצב; Tick שכבר החליט "להקשיח" יכול היה להחיל
    // recoveryenabled=No/SafeBoot מחדש אחרי השחרור, והשירות נמחק בלי שמישהו ישחרר. עכשיו נעילה אחת סביב כל הרצה, ו"תפס" שחרור-מלא שחוסם הקשחה מחדש
    private static readonly object _enforceLock = new();
    private static bool _releasedFull;

    /// <summary>אחרי כשל הסרה (השירות נשאר): מאפשר הקשחה מחדש</summary>
    internal static void AllowReharden() { lock (_enforceLock) { _releasedFull = false; _lastActive = null; } }

    /// <summary>null = לא הורצה (דילוג). נעילה + תפס שחרור. host ניתן להזרקה לבדיקות</summary>
    internal static Result EnforceLatched(IBootHost host, bool active, bool full)
    {
        lock (_enforceLock)
        {
            if (active && _releasedFull) return null; // ההסרה שחררה והשירות עומד להימחק: לא מחזירים הקשחה
            bool changed = _lastActive != active || full;
            if (!changed && ++_ticks < 60) return null;
            _ticks = 0;
            _lastActive = active;
            var r = Enforce(host, active, full);
            if (full) { _lastResult = r; if (!active && !r.Incomplete) _releasedFull = true; }
            return r;
        }
    }

    // ---------- מצב אבטחה (BitLocker / TPM / Secure Boot) ----------

    public sealed class Posture
    {
        /// <summary>0 כבוי, 1 מופעל, 2 לא ידוע (ProtectionStatus); null אם לא ניתן לקרוא</summary>
        public int? BitLockerProtection { get; set; }
        /// <summary>סוגי מגני מפתח (Win32_EncryptableVolume.GetKeyProtectors): 1=TPM, 4=TPM+PIN, 5=TPM+מפתח, 6=TPM+PIN+מפתח...</summary>
        public int[] KeyProtectorTypes { get; set; }
        public bool? TpmPresent { get; set; }
        public bool? TpmEnabled { get; set; }
        /// <summary>true/false; null = לא ידוע (למשל מנגנון Legacy BIOS בלי המפתח)</summary>
        public bool? SecureBoot { get; set; }
    }

    public sealed class Finding
    {
        public string Id { get; set; }
        /// <summary>ok | warn | bad | unknown</summary>
        public string Level { get; set; }
        public string Text { get; set; }
    }

    /// <summary>הופך מצב גולמי לממצאים בעברית שהמנהל רואה. טהור, ניתן לבדיקה.</summary>
    internal static List<Finding> BuildFindings(Posture p)
    {
        var list = new List<Finding>();
        if (p.BitLockerProtection is null)
            list.Add(new Finding { Id = "bitlocker", Level = "unknown", Text = "לא ניתן לקרוא את מצב BitLocker (נדרשות הרשאות מנהל/שירות)" });
        else if (p.BitLockerProtection == 1)
        {
            var t = p.KeyProtectorTypes ?? Array.Empty<int>();
            bool preboot = t.Any(x => x is 4 or 5 or 6);
            bool tpm = t.Contains(1);
            if (preboot) list.Add(new Finding { Id = "bitlocker", Level = "ok", Text = "BitLocker פעיל עם אימות לפני אתחול (PIN/מפתח)" });
            else if (tpm) list.Add(new Finding { Id = "bitlocker", Level = "warn", Text = "BitLocker פעיל עם TPM בלבד, בלי PIN: תוקף עם גישה פיזית עלול לעקוף. מומלץ PIN לפני אתחול" });
            else list.Add(new Finding { Id = "bitlocker", Level = "warn", Text = "BitLocker פעיל, אך לא זוהה מגן TPM/PIN סטנדרטי" });
        }
        else if (p.BitLockerProtection == 0)
            list.Add(new Finding { Id = "bitlocker", Level = "bad", Text = "BitLocker כבוי: הדיסק חשוף לקריאה/שינוי מחוץ ל-Windows" });
        else
            list.Add(new Finding { Id = "bitlocker", Level = "unknown", Text = "מצב BitLocker לא ידוע (הצפנה מושהית או נפח נעול)" });

        if (p.TpmPresent == false) list.Add(new Finding { Id = "tpm", Level = "bad", Text = "לא נמצא TPM" });
        else if (p.TpmPresent == true && p.TpmEnabled == false) list.Add(new Finding { Id = "tpm", Level = "bad", Text = "TPM קיים אך כבוי" });
        else if (p.TpmPresent == true) list.Add(new Finding { Id = "tpm", Level = "ok", Text = "TPM פעיל" });
        else list.Add(new Finding { Id = "tpm", Level = "unknown", Text = "לא ניתן לקרוא את מצב ה-TPM" });

        if (p.SecureBoot == true) list.Add(new Finding { Id = "secureBoot", Level = "ok", Text = "Secure Boot פעיל" });
        else if (p.SecureBoot == false) list.Add(new Finding { Id = "secureBoot", Level = "bad", Text = "Secure Boot כבוי" });
        else list.Add(new Finding { Id = "secureBoot", Level = "warn", Text = "Secure Boot לא נתמך/לא זוהה (ייתכן BIOS מסוג Legacy)" });
        return list;
    }

    /// <summary>הבדיקה החיה. כל חלק נקרא בנפרד, וכשל בחלק אחד לא מפיל את האחרים</summary>
    internal static Posture ProbePosture()
    {
        var p = new Posture();
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();
            string sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='" + sysDrive + "'"));
            foreach (ManagementObject v in s.Get())
                using (v)
                {
                    using (var o = v.InvokeMethod("GetProtectionStatus", null, null))
                        p.BitLockerProtection = Convert.ToInt32(o["ProtectionStatus"]);
                    try
                    {
                        using var ip = v.GetMethodParameters("GetKeyProtectors");
                        ip["KeyProtectorType"] = 0u; // 0 = כל הסוגים
                        using var o = v.InvokeMethod("GetKeyProtectors", ip, null);
                        var ids = o["VolumeKeyProtectorID"] as string[] ?? Array.Empty<string>();
                        var types = new List<int>();
                        foreach (string id in ids)
                        {
                            using var ti = v.GetMethodParameters("GetKeyProtectorType");
                            ti["VolumeKeyProtectorID"] = id;
                            using var to = v.InvokeMethod("GetKeyProtectorType", ti, null);
                            types.Add(Convert.ToInt32(to["KeyProtectorType"]));
                        }
                        p.KeyProtectorTypes = types.ToArray();
                    }
                    catch (Exception ex) { Config.Log("boot posture protectors: " + ex.Message); }
                    break;
                }
        }
        catch (Exception ex) { Config.Log("boot posture bitlocker: " + ex.Message); }

        try
        {
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue FROM Win32_Tpm");
            p.TpmPresent = false;
            foreach (ManagementObject o in s.Get())
                using (o) { p.TpmPresent = true; p.TpmEnabled = Convert.ToBoolean(o["IsEnabled_InitialValue"]); }
        }
        catch (Exception ex) { p.TpmPresent = null; Config.Log("boot posture tpm: " + ex.Message); }

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            p.SecureBoot = k?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : null;
        }
        catch (Exception ex) { Config.Log("boot posture secureboot: " + ex.Message); }
        return p;
    }

    private static string PostureFile => Path.Combine(HardLock.DataDir, "boot-posture.json");
    private static string StateFile => Path.Combine(HardLock.DataDir, "boothardening.json");

    /// <summary>השירות כותב צילום מצב (קריאה בלבד), כי הגשר בחשבון המשתמש לא מקבל גישה ל-WMI של BitLocker</summary>
    private static void WritePosture(IBootHost host)
    {
        try { File.WriteAllText(PostureFile, JsonSerializer.Serialize(ProbePosture(), Server.Json)); }
        catch (Exception ex) { host.Log("boot-hardening: כתיבת צילום מצב נכשלה: " + ex.Message); }
    }

    /// <summary>לשילוב ב-/bios/status: ממצאים + מצב האכיפה האחרון. בגשר: בדיקה חיה, ואם נכשלה, צילום המצב של השירות</summary>
    internal static JsonObject StatusNode()
    {
        var node = new JsonObject();
        Posture p = null;
        try { p = ProbePosture(); } catch { }
        if (p == null || p.BitLockerProtection == null)
        {
            try
            {
                if (File.Exists(PostureFile))
                {
                    var saved = JsonSerializer.Deserialize<Posture>(File.ReadAllText(PostureFile), Server.Json);
                    if (saved != null) { p = saved; node["source"] = "service-snapshot"; }
                }
            }
            catch { }
        }
        p ??= new Posture();
        node["source"] ??= "live";
        node["posture"] = JsonSerializer.SerializeToNode(p, Server.Json);
        node["findings"] = JsonSerializer.SerializeToNode(BuildFindings(p), Server.Json);
        try
        {
            if (File.Exists(StateFile) && JsonNode.Parse(File.ReadAllText(StateFile)) is JsonObject st)
            {
                var inc = st["incomplete"] as JsonArray;
                node["enforcement"] = new JsonObject
                {
                    ["active"] = st["active"]?.DeepClone(),
                    ["updated"] = st["updated"]?.DeepClone(),
                    ["incomplete"] = inc != null && inc.Count > 0,
                    ["problems"] = inc?.DeepClone() ?? new JsonArray(),
                    ["message"] = inc != null && inc.Count > 0 ? "הקשחת אתחול לא הושלמה" : null,
                };
            }
        }
        catch { }
        return node;
    }

    // ---------- המימוש האמיתי ----------

    private sealed class RealHost : IBootHost
    {
        private const string SafeBootRoot = @"SYSTEM\CurrentControlSet\Control\SafeBoot\";

        public string Run(string file, string args) => Config.Run(file, args, 30000);

        public bool SafeBootExists(string set, string name)
        {
            using var k = Registry.LocalMachine.OpenSubKey(SafeBootRoot + set + "\\" + name);
            return k != null;
        }

        public bool SafeBootCreate(string set, string name)
        {
            try
            {
                using var k = Registry.LocalMachine.CreateSubKey(SafeBootRoot + set + "\\" + name);
                k.SetValue("", "Service", RegistryValueKind.String);
                return true;
            }
            catch (Exception ex) { Config.Log("safeboot create: " + ex.Message); return false; }
        }

        public bool SafeBootDelete(string set, string name)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(SafeBootRoot + set + "\\" + name, false); return true; }
            catch (Exception ex) { Config.Log("safeboot delete: " + ex.Message); return false; }
        }

        // כתיבה אטומית עם Flush(true) וגיבוי (.bak) של התקין האחרון, וקריאה שחוזרת לגיבוי כשהראשי פגום (qa-final-a #7): אחרת הפסקת חשמל
        // מחקה את רשומות ה-orig ("פגום, מתחילים נקי") ושחזור bcd אבד
        public string ReadState() => StateStore.Read(StateFile, out _, out _)?.ToJsonString();

        public void WriteState(string json) => StateStore.WriteAtomic(StateFile, json);

        public void Log(string line) => HardLock.SvcLog(line); // רץ בשירות (SYSTEM): היומן של השירות
    }
}
