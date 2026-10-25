using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// נעילה מוקשחת (בעיה #1, גרסה 3.0.0) — רכיב שירות Windows שרץ כ-SYSTEM.
///
/// הגשר הרגיל רץ בהרשאות המשתמש, ולכן משתמש נעול יכול (בעקיפין) לסגור אותו או לערוך את config.json.
/// השירות OtzariaBridgeService, כשהוא מותקן (אופציונלי, דורש אישור מנהל פעם אחת):
///  • מחזיק את מצב הנעילה ואת גיבוב הסיסמה ב-%ProgramData%\OtzariaBridge (רק SYSTEM ומנהלים כותבים).
///  • מפעיל מחדש את הגשר בכל חשבון מחובר אם נסגר — כולל חשבון Windows אחר.
///  • מחזיר מדיניות (מנהל המשימות ועורך הרישום חסומים) אם נמחקה, בכל החשבונות המחוברים.
///  • חוסם התקני אחסון USB (אופציונלי).
/// השירות לא נוגע במעטפת Windows ולא בכניסה למערכת (לקח מ-#8), ואינו רץ במצב בטוח.
/// הגשר מדבר איתו דרך Named Pipe מקומי.
/// </summary>
public static class HardLock
{
    public const string ServiceName = "OtzariaBridgeService";
    public const string PipeName = "OtzariaBridgeService";

    public static string ProgDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OtzariaBridge");
    public static string ServiceExe => Path.Combine(ProgDir, "OtzariaBridge.exe");
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OtzariaBridge");

    // ================= צד הגשר (משתמש) =================

    public static void Register()
    {
        Server.Route("/kiosk/hard/status", _ => Status(fresh: true));
        Server.Route("/kiosk/hard/install", Install);
        Server.Route("/kiosk/hard/uninstall", Uninstall);
        Server.Route("/kiosk/hard/settings", Settings);
    }

    /// <summary>שליחת פקודה לשירות. null = השירות לא זמין</summary>
    public static JsonObject Call(JsonObject req, int timeoutMs = 2500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            // לפני שליחת סיסמה: השרת חייב להיות תהליך השירות (SYSTEM, ServiceExe) ולא תהליך משתמש שיצר את ה-pipe קודם
            if (!PipePeer.ServerIsService(pipe.SafePipeHandle.DangerousGetHandle()))
            {
                Config.Log("hardlock: שרת ה-pipe אינו תהליך השירות המותקן — לא נשלח מידע (fail-closed)");
                return null;
            }
            var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(pipe, Encoding.UTF8);
            w.WriteLine(req.ToJsonString());
            var readTask = r.ReadLineAsync();
            // WHY: בפסק זמן ה-using סוגר את ה-pipe בזמן שהקריאה תלויה, והיא נופלת ב-ObjectDisposedException שאף אחד לא צופה בו =>
            // UnobservedTaskException (הוצף ב-136 דיווחים אוטומטיים). צופים בחריג כדי שלא יגיע ל-finalizer.
            _ = readTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            // בעיה #14 סעיף 2: רצפת המתנה קשיחה, לא 15ש' שחוסמות חוט HTTP גם כשנדרש timeout קצר בהרבה
            if (!readTask.Wait(Math.Max(timeoutMs, 3000))) return null;
            return JsonNode.Parse(readTask.Result ?? "") as JsonObject;
        }
        catch { return null; }
    }

    /// <summary>תשובת שגיאה מהשירות → חריגה עם אותו קוד</summary>
    private static JsonObject Must(JsonObject res)
    {
        if (res == null) throw new BridgeException("unsupported", "רכיב ההקשחה אינו זמין", 503);
        if (res["ok"]?.GetValue<bool>() == true) return res;
        string code = res["code"]?.ToString() ?? "internal";
        int status = code switch { "wrong_password" => 401, "two_factor" => 401, "forbidden" => 403, "too_many" => 429, "bad_request" => 400, _ => 500 };
        throw new BridgeException(code == "too_many" ? "wrong_password" : code, res["message"]?.ToString() ?? "שגיאה ברכיב ההקשחה", status);
    }

    private static JsonObject _cache;
    private static bool _installed;
    private static DateTime _installedAt = DateTime.MinValue;
    private static DateTime _cacheAt = DateTime.MinValue;

    public static bool ServiceInstalled()
    {
        try { return ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private static JsonObject ServiceStatus(bool fresh = false)
    {
        if (!fresh && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(3)) return _cache;
        // בלי שירות מותקן לא ממתינים לחיבור (בדיקת ההתקנה נשמרת ל-30 שניות)
        if (DateTime.UtcNow - _installedAt > TimeSpan.FromSeconds(fresh ? 0 : 30)) { _installed = ServiceInstalled(); _installedAt = DateTime.UtcNow; }
        var res = _installed ? Call(new JsonObject { ["op"] = "status" }, 1000) : null;
        // תמונת המדיניות מפוענחת כאן, בחוט שקרא, ונשמרת כאובייקט שאינו משתנה — הקוראים (השומר, בקשות HTTP)
        // לא נוגעים ב-JsonNode המשותף, שאינו בטוח לקריאה מקבילה
        // pipe לא עונה והשירות מותקן: שומרים את תמונת המדיניות האחרונה (fail-closed) במקום ליפול ל-config.json שהמשתמש כותב אליו;
        // הפתיחה הזמנית מתאפסת (לא מניחים שהיא עדיין בתוקף)
        if (res != null || !_installed) _policy = PolicySnapshot.From(res?["policy"] as JsonObject);
        _temp = TempSnapshot.From(res?["tempUnlock"] as JsonObject, res?["twoFactor"]?.GetValue<bool>() == true);
        _cache = res;
        _cacheAt = DateTime.UtcNow;
        return _cache;
    }

    /// <summary>
    /// השירות הוא מקור האמת לסיסמה ולמצב הנעילה. כשהוא מותקן אבל ה-pipe לא עונה (קרס, נתקע, או מוצף בחיבורים) — עדיין "זמין":
    /// הקוראים (Kiosk.Verify ועוד) יקבלו 503 ולא ייפלו ל-hash המקומי ב-config.json, שהמשתמש יכול לערוך (fail-closed).
    /// שירות שמותקן ועונה אך עדיין לא הוגדר (configured=false) — לא זמין, כמו קודם (התקנה שנקטעה באמצע).
    /// חירום: --unlock (קוד שחזור) ממשיך לעבוד מקומית גם כשהשירות למטה.
    /// </summary>
    public static bool Available => DecideAvailable(ServiceStatus()?["configured"]?.GetValue<bool>(), _installed);

    /// <summary>configuredFromPipe: null = ה-pipe לא ענה. installed: השירות רשום ב-SCM</summary>
    internal static bool DecideAvailable(bool? configuredFromPipe, bool installed) => configuredFromPipe ?? installed;

    // ---------- מדיניות הנעילה שהשירות מחזיק ----------
    // config.json נמצא בפרופיל של המשתמש המחובר וכתיב לו, ולכן הגדרות שהנעילה אוכפת (רשימת התוכנות
    // המותרות, נתיב אוצריא, מדיניות סייר הקבצים) אינן יכולות להסתמך עליו בלבד. כשרכיב ההקשחה מותקן,
    // העותק שלו (ב-%ProgramData%, כתיב רק ל-SYSTEM ולמנהלים) הוא מקור האמת — בדיוק כמו הסיסמה ומצב הנעילה.
    // שדה null בתמונה = השירות עוד לא קיבל את ההגדרה (מצב מגרסה קודמת), ואז נשארים עם config.json
    // עד הסנכרון הבא בסיסמה (עדכון רכיב ההקשחה או שמירת ההגדרה).

    public sealed class PolicySnapshot
    {
        public string[] Programs { get; private init; }        // כל רשומה: אובייקט JSON כטקסט, כמו ב-kiosk.programs
        public bool? AutoTorah { get; private init; }
        public string[] AutoTorahKeywords { get; private init; }
        public string OtzariaPath { get; private init; }        // "" = זיהוי אוטומטי (בלי נתיב מוגדר)
        /// <summary>WHY (T1): SHA-256 של אוצריא שהשירות מחזיק (נשמר בסיסמה). אוצריא במיקום כתיב תופעל רק אם הקובץ תואם. "" = אין נעיצה</summary>
        public string OtzariaSha256 { get; private init; }
        public string FxMode { get; private init; }
        public string[] FxRoots { get; private init; }
        public int? FxUnlockMinutes { get; private init; }

        internal static PolicySnapshot From(JsonObject o)
        {
            if (o == null) return null;
            static string[] Strs(JsonNode n, bool json) => n is JsonArray a
                ? a.Where(x => x != null).Select(x => json ? x.ToJsonString() : x.ToString()).ToArray()
                : null;
            try
            {
                var fx = o["fxPolicy"] as JsonObject;
                return new PolicySnapshot
                {
                    Programs = Strs(o["programs"], json: true),
                    AutoTorah = o["autoTorahPrograms"] is JsonValue at ? at.GetValue<bool>() : null,
                    AutoTorahKeywords = Strs(o["autoTorahKeywords"], json: false),
                    OtzariaPath = o["otzariaPath"]?.ToString(),
                    OtzariaSha256 = o["otzariaSha256"]?.ToString(),
                    FxMode = fx?["mode"]?.ToString(),
                    FxRoots = fx == null ? null : Strs(fx["roots"], json: false) ?? Array.Empty<string>(),
                    FxUnlockMinutes = fx?["unlockMinutes"] is JsonValue fm ? fm.GetValue<int>() : null,
                };
            }
            catch { return null; }
        }
    }

    private static volatile PolicySnapshot _policy;

    /// <summary>המדיניות שהשירות מחזיק, או null כשהשירות אינו זמין (ואז האכיפה לפי config.json, כמו בלי רכיב הקשחה)</summary>
    public static PolicySnapshot Policy => Available ? _policy : null;

    // ---------- פתיחה זמנית (השירות מחזיק את הסוד ואת זמן הסיום, ומחזיר את הנעילה בעצמו) ----------

    public sealed class TempSnapshot
    {
        public bool Enabled { get; private init; }
        public bool Configured { get; private init; }
        public int Minutes { get; private init; }
        public bool TwoFactor { get; private init; }
        public DateTimeOffset? Until { get; private init; }

        internal static TempSnapshot From(JsonObject o, bool twoFactor)
        {
            if (o == null) return null; // שירות ישן שאינו מכיר פתיחה זמנית
            try
            {
                return new TempSnapshot
                {
                    Enabled = o["enabled"]?.GetValue<bool>() == true,
                    Configured = o["configured"]?.GetValue<bool>() == true,
                    Minutes = TempUnlock.ClampMinutes(o["minutes"] is JsonValue mv && mv.TryGetValue<int>(out int m) ? m : TempUnlock.DefaultMinutes),
                    TwoFactor = twoFactor,
                    Until = TempUnlock.ParseUntil(o["until"]?.ToString()),
                };
            }
            catch { return null; }
        }
    }

    private static volatile TempSnapshot _temp;

    /// <summary>מצב הפתיחה הזמנית בשירות; null כשהשירות אינו זמין או שגרסתו ישנה מדי</summary>
    public static TempSnapshot Temp => Available ? _temp : null;

    /// <summary>האם האימות הדו-שלבי פעיל בשירות (מקור האמת כשהוא מותקן)</summary>
    public static bool ServiceTwoFactor => ServiceStatus()?["twoFactor"]?.GetValue<bool>() == true;

    /// <summary>הגדרות הפתיחה הזמנית → השירות. hash/salt null = הסיסמה לא משתנה</summary>
    public static void SetTempUnlock(string password, bool enabled, int minutes, string hash, string salt)
    {
        var f = new JsonObject { ["tempUnlockEnabled"] = enabled, ["tempUnlockMinutes"] = minutes };
        if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(salt)) { f["tempUnlockHash"] = hash; f["tempUnlockSalt"] = salt; }
        PushSettings(password, f);
    }

    /// <summary>פתיחה זמנית בסיסמה הנפרדת. בלי Windows Hello — זו בדיוק הסיסמה שנמסרת למי שאין לו את זהות המנהל</summary>
    public static void TempUnlockStart(string secret)
    {
        Must(Call(new JsonObject { ["op"] = "temp-unlock", ["password"] = secret ?? "" }));
        _cacheAt = DateTime.MinValue;
        _sawActive = false; // הגשר משתחרר עכשיו; כשהשירות ינעל שוב בתום הזמן, הסנכרון ינעל גם אותו
    }

    /// <summary>סיום מוקדם של הפתיחה הזמנית (רק מחזיר את הנעילה, ולכן בלי סיסמה)</summary>
    public static void TempUnlockEnd()
    {
        Must(Call(new JsonObject { ["op"] = "temp-unlock-end" }));
        _cacheAt = DateTime.MinValue;
    }

    private static void PushSettings(string password, JsonObject fields)
    {
        fields["op"] = "settings";
        fields["password"] = password ?? "";
        // "settings" בשירות דורש Windows Hello כשהאימות הדו-שלבי פעיל — כמו SetSchedule/SetTwoFactor
        Must(CallSensitive(fields, password));
        _cacheAt = DateTime.MinValue;
    }

    /// <summary>רשימת התוכנות המותרות (הידנית) והגדרות הזיהוי האוטומטי → השירות</summary>
    public static void SetPrograms(string password, JsonArray programs, bool autoTorah, IEnumerable<string> keywords) =>
        PushSettings(password, new JsonObject
        {
            ["programs"] = programs,
            ["autoTorahPrograms"] = autoTorah,
            ["autoTorahKeywords"] = new JsonArray((keywords ?? Array.Empty<string>()).Select(k => (JsonNode)k).ToArray()),
        });

    /// <summary>נתיב אוצריא → השירות. מחרוזת ריקה = זיהוי אוטומטי</summary>
    public static void SetOtzariaPath(string password, string path, string sha256 = null)
    {
        var f = new JsonObject { ["otzariaPath"] = path ?? "" };
        // WHY (T1): הנעיצה נשלחת באותה בקשה (בסיסמה) כדי שנתיב ונעיצה לא יתפצלו; בלי sha השירות מנקה נעיצה ישנה של נתיב אחר
        if (sha256 != null) f["otzariaSha256"] = sha256;
        PushSettings(password, f);
    }

    /// <summary>מדיניות סייר הקבצים → השירות</summary>
    public static void SetFxPolicy(string password, string mode, IEnumerable<string> roots, int unlockMinutes) =>
        PushSettings(password, new JsonObject
        {
            ["fxPolicy"] = new JsonObject
            {
                ["mode"] = mode ?? "off",
                ["roots"] = new JsonArray((roots ?? Array.Empty<string>()).Select(r => (JsonNode)r).ToArray()),
                ["unlockMinutes"] = unlockMinutes,
            },
        });

    /// <summary>
    /// מעביר לשירות את המדיניות הנוכחית (העותק של השירות אם כבר יש לו, אחרת config.json — הגירה חד-פעמית
    /// ממצב ישן). נקרא בהתקנה/עדכון של רכיב ההקשחה, שבהם המנהל כבר הזין סיסמה.
    /// </summary>
    private static void SyncPolicy(string password)
    {
        try { OtzariaBridge.Programs.SyncToService(password); }
        catch (Exception ex) { Config.Log("hardlock sync programs: " + ex.Message); }
        try { FxPolicy.SyncToService(password); }
        catch (Exception ex) { Config.Log("hardlock sync fx policy: " + ex.Message); }
        try { Kiosk.SyncTempUnlockToService(password); }
        catch (Exception ex) { Config.Log("hardlock sync temp unlock: " + ex.Message); }
        try
        {
            var pol = Policy;
            if (pol != null && pol.OtzariaPath == null)
                SetOtzariaPath(password, Config.Get<string>("kiosk.otzariaPath", "") ?? "");
        }
        catch (Exception ex) { Config.Log("hardlock sync otzariaPath: " + ex.Message); }
        // WHY (T1): הגירה חד-פעמית: הגדרות קיימות (בלי חתימה) נחתמות עכשיו, כשהמנהל אימת סיסמה — אחרת עדכון השירות היה מפיל
        // את כל מפתחות האבטחה לברירות מחדל מחמירות עד שמירת הגדרות ידנית
        try { ConfigSeal.Reset(); Kiosk.SealConfig(); }
        catch (Exception ex) { Config.Log("hardlock sync seal: " + ex.Message); }
    }

    /// <summary>האם השירות אומר שהנעילה פעילה</summary>
    public static bool ServiceActive => ServiceStatus()?["active"]?.GetValue<bool>() == true;

    public static object Status(bool fresh = false)
    {
        var s = ServiceStatus(fresh);
        bool installed = s != null || ServiceInstalled();
        string sv = s?["version"]?.ToString();
        return new
        {
            installed,
            running = s != null,
            configured = s?["configured"]?.GetValue<bool>() == true,
            active = s?["active"]?.GetValue<bool>() == true,
            blockUsbStorage = s?["blockUsbStorage"]?.GetValue<bool>() == true,
            twoFactor = s?["twoFactor"]?.GetValue<bool>() == true,
            version = sv,
            needsUpdate = sv != null && sv != Program.Version,
            sessions = s?["sessions"]?.GetValue<int>() ?? 0,
        };
    }

    /// <summary>
    /// פקודה רגישה (ביטול נעילה/סיסמה/הסרה/הגדרות): אם השירות דורש אימות דו-שלבי הוא עונה "two_factor";
    /// אז מפעילים Windows Hello דרך השירות (תהליך עזר בסשן המשתמש, כי סשן 0 אינו יכול להציג UI) ושולחים שוב
    /// עם אישור חד-פעמי. כך אי אפשר לדלג על Hello מול הצינור, גם אם יודעים את הסיסמה.
    /// </summary>
    private static JsonObject CallSensitive(JsonObject req, string password, int timeoutMs = 2500)
    {
        var res = Call(req, timeoutMs);
        if (res?["code"]?.ToString() == "two_factor")
        {
            req["hello"] = RunHello(password);
            res = Call(req, timeoutMs);
        }
        return res;
    }

    private static string RunHello(string password)
    {
        var st = Must(Call(new JsonObject { ["op"] = "hello-start", ["password"] = password ?? "" }));
        string nonce = st["nonce"]?.ToString();
        var deadline = DateTime.UtcNow.AddSeconds(75);
        while (DateTime.UtcNow < deadline)
        {
            // חלון Windows Hello (CredentialUIBroker) אינו "תוכנה מותרת": בלי חסד השומר היה מחזיר את אוצריא לחזית
            // כל 2 שניות ומסתיר את חלון האימות. החסד קצר (30 שניות) ומתחדש רק כל עוד ממתינים לאימות
            Programs.GrantForegroundGrace();
            Thread.Sleep(700);
            string s = Call(new JsonObject { ["op"] = "hello-status", ["nonce"] = nonce }, 2500)?["state"]?.ToString();
            if (s == "approved") return nonce;
            if (s is "failed" or "expired") break;
        }
        throw new BridgeException("two_factor", "נדרש אימות Windows Hello (זיהוי פנים/טביעת אצבע/PIN) והוא נכשל או בוטל", 401);
    }

    /// <summary>
    /// מסנכרן את דגל האימות הדו-שלבי אל השירות, כשהוא מותקן — כך ש-Hello נאכף גם מול הצינור עצמו,
    /// לא רק בשכבת ה-HTTP של הגשר (ביקורת #30 ממצא 1). עשוי להציג חלון Hello נוסף בהפעלה ראשונה.
    /// </summary>
    public static void SetTwoFactor(string password, bool on)
    {
        Must(CallSensitive(new JsonObject { ["op"] = "settings", ["password"] = password ?? "", ["twoFactor"] = on }, password));
        _cacheAt = DateTime.MinValue;
    }

    public static void Verify(string password, bool allowRecovery) =>
        Must(Call(new JsonObject { ["op"] = "verify", ["password"] = password ?? "", ["allowRecovery"] = allowRecovery }));

    public static void Enable(string password, string otzariaPath)
    {
        Must(Call(new JsonObject { ["op"] = "enable", ["password"] = password ?? "", ["otzariaPath"] = otzariaPath ?? "" }));
        _cacheAt = DateTime.MinValue;
        _sawActive = true;
    }

    public static void Disable(string password)
    {
        Must(CallSensitive(new JsonObject { ["op"] = "disable", ["password"] = password ?? "" }, password));
        _cacheAt = DateTime.MinValue;
        _sawActive = false;
    }

    public static void SetPassword(string oldPassword, string newPassword) =>
        Must(CallSensitive(new JsonObject { ["op"] = "set-password", ["old"] = oldPassword ?? "", ["new"] = newPassword ?? "" }, oldPassword));

    /// <summary>העברת חלון התזמון לשירות (הוא המתזמן כשהוא מותקן)</summary>
    public static void SetSchedule(string password, bool enabled, string start, string end) =>
        Must(CallSensitive(new JsonObject
        {
            ["op"] = "settings", ["password"] = password ?? "",
            ["scheduleEnabled"] = enabled, ["scheduleStart"] = start ?? "", ["scheduleEnd"] = end ?? "",
        }, password));

    /// <summary>
    /// דילוג על הנעילה המתוזמנת הקרובה עד הזמן הנתון (מ"ביטול הנעילה" באזהרה). עד 36 שעות, ולכן דורש
    /// גם 2FA כשהוא פעיל — אחרת סיסמה לבדה הייתה יכולה לנטרל את התזמון לצמיתות (חזרה על הדילוג כל 36ש').
    /// </summary>
    public static void Skip(string password, string untilIso) =>
        Must(CallSensitive(new JsonObject { ["op"] = "skip", ["password"] = password ?? "", ["until"] = untilIso ?? "" }, password));

    /// <summary>קוד שחזור משורת הפקודה: מבטל גם בשירות (פטור מ-2FA — ביטול חירום)</summary>
    public static bool EmergencyDisable(string code)
    {
        var r = Call(new JsonObject { ["op"] = "disable", ["password"] = code ?? "" });
        return r?["ok"]?.GetValue<bool>() == true;
    }

    /// <summary>
    /// בקשת טוקן שחרור חירום חד-פעמי מהשירות, אחרי אימות קוד השחזור בפייפ (ר' RecoveryTokens). null = השירות לא ענה/לא מכיר את הפקודה/הקוד שגוי
    /// </summary>
    public static string RecoveryTokenMint(string code)
    {
        var r = Call(new JsonObject { ["op"] = "recovery-token", ["code"] = code ?? "" });
        return r?["ok"]?.GetValue<bool>() == true ? r["token"]?.ToString() : null;
    }

    /// <summary>צריכת הטוקן מול השירות. true = תקף (ונצרך), false = נדחה, null = השירות לא ענה/לא מכיר את הפקודה (קורא מנסה שוב / נשאר נעול)</summary>
    public static bool? RecoveryTokenConsume(string token)
    {
        var r = Call(new JsonObject { ["op"] = "recovery-consume", ["token"] = token ?? "" });
        if (r == null) return null;
        if (r["ok"]?.GetValue<bool>() == true) return true;
        return r["code"]?.ToString() == "unknown_op" ? null : false;
    }

    /// <summary>האם הטקסט הוא קוד השחזור, לפי השירות (עותק ה-hash המקומי נמחק). null = השירות לא ענה/לא מכיר את הפקודה</summary>
    public static bool? VerifyRecoveryOnly(string code)
    {
        var r = Call(new JsonObject { ["op"] = "verify", ["password"] = code ?? "", ["recoveryOnly"] = true });
        if (r == null) return null;
        if (r["ok"]?.GetValue<bool>() == true) return true;
        return r["code"]?.ToString() is "unknown_op" or "too_many" ? null : false;
    }

    /// <summary>האם השירות מחזיק קוד שחזור (שדה status "hasRecovery", שירות ישן: false). רק אז מותר למחוק את העותק המקומי</summary>
    public static bool ServiceHasRecovery()
    {
        try { return ServiceStatus()?["hasRecovery"]?.GetValue<bool>() == true; } catch { return false; }
    }

    public static string UninstallWithPassword(string password)
    {
        JsonObject r;
        try { r = CallSensitive(new JsonObject { ["op"] = "uninstall", ["password"] = password ?? "" }, password, 5000); }
        catch (BridgeException ex) { return ex.Message; }
        if (r == null) return null; // אין שירות
        return r["ok"]?.GetValue<bool>() == true ? null : r["message"]?.ToString() ?? "סיסמה שגויה";
    }

    private static object Install(JsonElement b)
    {
        if (string.IsNullOrEmpty(Config.Get<string>("kiosk.passwordHash", null)))
            throw BridgeException.BadRequest("יש להגדיר סיסמת נעילה לפני התקנת רכיב ההקשחה");
        Kiosk.RequirePassword(b.Str("password"));
        try
        {
            // גיבוב ה-exe הנוכחי מועבר לתהליך המורם, שמאמת שהוא מעתיק בדיוק את הקובץ הזה (ר' InstallTrust)
            string srcHash = InstallTrust.SelfSha256() ?? throw new BridgeException("internal", "לא ניתן לחשב גיבוב של הגשר הרץ", 500);
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--install-service --src-sha256 " + srcHash + " --parent-pid " + Environment.ProcessId)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (!p.WaitForExit(180_000)) throw new BridgeException("timeout", "ההתקנה לא הסתיימה בזמן", 504);
            if (p.ExitCode != 0)
                throw new BridgeException("internal", "התקנת רכיב ההקשחה נכשלה (קוד " + p.ExitCode + "). פרטים ב-" + Path.Combine(DataDir, "service.log"), 500);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new BridgeException("cancelled", "ההתקנה בוטלה (לא אושרה הרשאת מנהל)", 400);
        }

        // ממתינים שהשירות יעלה. "setup" כבר בוצע בפועל מתוך InstallService (תהליך מורם - רק הוא
        // עובר את בדיקת ההרשאות של op=setup) - כאן זה רק רשת ביטחון, לא Must, כדי שגרסת שירות ישנה
        // יותר (שעוד לא עשתה setup בעצמה) לא תישבר, ושקריאה כפולה (כבר מוגדר) לא תפיל את ההתקנה
        JsonObject st = null;
        for (int i = 0; i < 20 && st == null; i++) { st = Call(new JsonObject { ["op"] = "status" }, 1000); if (st == null) Thread.Sleep(500); }
        if (st == null) throw new BridgeException("internal", "השירות הותקן אבל לא עלה", 500);
        if (st["configured"]?.GetValue<bool>() != true)
        {
            var setupRes = Call(new JsonObject
            {
                ["op"] = "setup",
                ["passwordHash"] = Config.Get<string>("kiosk.passwordHash", ""),
                ["salt"] = Config.Get<string>("kiosk.salt", ""),
                ["recoveryHash"] = Config.Get<string>("kiosk.recoveryHash", ""),
                ["recoverySalt"] = Config.Get<string>("kiosk.recoverySalt", ""),
                ["twoFactor"] = Config.Get("kiosk.twoFactor", false),
            });
            if (setupRes?["ok"]?.GetValue<bool>() != true)
                Config.Log("hardlock install: setup (רשת ביטחון) נכשל: " + (setupRes?["message"]?.ToString() ?? "אין תשובה"));
        }
        if (Kiosk.IsActive) Enable(b.Str("password"), Kiosk.OtzariaPath());
        if (Kiosk.ScheduleEnabled)
            try { SetSchedule(b.Str("password"), true, Kiosk.ScheduleStart, Kiosk.ScheduleEnd); }
            catch (Exception ex) { Config.Log("hardlock schedule: " + ex.Message); }
        _cacheAt = DateTime.MinValue;
        SyncPolicy(b.Str("password"));
        Config.Log("hardlock: רכיב ההקשחה הותקן/עודכן");
        return Status(fresh: true);
    }

    private static object Uninstall(JsonElement b)
    {
        Must(CallSensitive(new JsonObject { ["op"] = "uninstall", ["password"] = b.Str("password") ?? "" }, b.Str("password"), 5000));
        _sawActive = false;
        Thread.Sleep(1500);
        return Status(fresh: true);
    }

    private static object Settings(JsonElement b)
    {
        var req = new JsonObject { ["op"] = "settings", ["password"] = b.Str("password") ?? "" };
        if (b.Has("blockUsbStorage")) req["blockUsbStorage"] = b.Bool("blockUsbStorage");
        if (b.Has("twoFactor")) req["twoFactor"] = b.Bool("twoFactor");
        Must(CallSensitive(req, b.Str("password")));
        return Status(fresh: true);
    }

    // ---------- סנכרון: השירות הוא מקור האמת ----------

    private static System.Threading.Timer _sync;
    private static bool _sawActive;
    private static int _inactiveTicks;

    /// <summary>כל 5 שניות: אם השירות אומר "נעול" והגשר לא — נועלים (גם אחרי עריכת config.json או בחשבון אחר)</summary>
    public static void StartSync()
    {
        _sync ??= new System.Threading.Timer(_ =>
        {
            try
            {
                var s = ServiceStatus(fresh: true);
                SyncOwnDacl(s);
                if (s == null) { RestoreOrphanedPolicies(); return; }
                bool active = s["active"]?.GetValue<bool>() == true;
                if (active)
                {
                    _sawActive = true;
                    _inactiveTicks = 0;
                    if (!Kiosk.IsActive) Kiosk.ForceActivate(s["otzariaPath"]?.ToString());
                }
                else
                {
                    // _sawActive לבדו לא הספיק: אם הגשר עלה כשהוא כבר נעול מקומית (kiosk.active בקובץ) והשירות כבר לא
                    // פעיל (נגמר חלון תזמון, או בוטל כשהגשר היה כבוי), הדגל נשאר false לעולם ואף אחד לא שחרר - נעילה תקועה.
                    // לכן משחררים גם כשהשירות מוגדר (יש לו סיסמה) ואומר "לא פעיל" ב-3 סבבים רצופים (15 שניות, נגד
                    // מרוץ מול הפעלה שזה עתה התחילה), בלי תלות ב-_sawActive. שירות שלא הוגדר = נעילה מקומית בלבד, לא נוגעים
                    bool configured = s["configured"]?.GetValue<bool>() == true;
                    if (_sawActive || (configured && Kiosk.IsActive))
                    {
                        if (++_inactiveTicks >= (_sawActive ? 1 : 3))
                        {
                            _sawActive = false;
                            _inactiveTicks = 0;
                            // "לא פעיל" בגלל פתיחה זמנית (lockOn עדיין true): משתחררים עד שהשירות ינעל שוב בתום הזמן
                            bool temporary = s["lockOn"]?.GetValue<bool>() == true;
                            if (Kiosk.IsActive) Kiosk.ReleaseFromService(temporary);
                        }
                    }
                    else _inactiveTicks = 0;
                }
            }
            catch (Exception ex) { Config.Log("hardlock sync: " + ex.Message); }
        }, null, 3000, 5000);
    }

    // ---------- DACL של תהליך הגשר (מוקשח תמיד, לא משתחרר לעולם) ----------
    // הגשר שהשירות מפעיל נולד מוקשח (Native.Launch) ונשאר כך. WHY: שחרור ה-DACL כשהנעילה לא נאכפת השאיר חלון שבו משתמש מחזיק
    // handle מלא ומשתמש בו אחר כך (הזרקה/הריגה אחרי חזרת הנעילה). יציאה/הסרה מורשית עוברת בבקשה לשירות (op=stop-bridges כשהנעילה
    // לא נאכפת, uninstall בסיסמה) או ב-"יציאה" מהמגש (הגשר יוצא בעצמו) — לא ב-taskkill. מנהל מורם/SYSTEM תמיד יכולים להרוג.

    private static readonly bool FromService = Environment.GetCommandLineArgs().Contains("--from-service");
    private static bool? _daclHardened;

    internal static bool? DecideDacl(bool fromService, JsonObject status, bool installed)
    {
        if (!fromService) return null; // גשר שהמשתמש הפעיל בעצמו: לא נוגעים (השירות הורג אותו ומעלה גשר מוגן כשהנעילה נאכפת)
        return true; // תמיד מוקשח, גם כשהנעילה לא נאכפת / השירות לא עונה / הוסר
    }

    private static void SyncOwnDacl(JsonObject status)
    {
        bool? want = DecideDacl(FromService, status, ServiceInstalled());
        if (want == null || _daclHardened == want) return;
        if (ProcessDacl.ApplyToSelf(want.Value))
        {
            _daclHardened = want;
            Config.Log("hardlock: DACL של תהליך הגשר " + (want.Value ? "הוקשח" : "שוחרר"));
        }
    }

    /// <summary>
    /// בעיה #14 סעיף 1: אם השירות הוסר ישירות (למשל "sc delete" בלי לעבור דרך SelfUninstall), אף אחד לא
    /// מריץ יותר את EnforcePolicies(false) על ה-SIDים שסומנו ב-touched, ומדיניות DisableTaskMgr/
    /// DisableRegistryTools נשארת תקועה אצל המשתמש בלי שום נעילה פעילה בפועל.
    /// הגשר (בהרשאות המשתמש הרגילות) לא יכול לגעת ב-HKEY_USERS של חשבונות אחרים, אבל HKEY_CURRENT_USER
    /// של אותו משתמש הוא בדיוק אותה היברידה — ולכן מנקים כאן. חשבונות אחרים ינוקו ממילא אוטומטית אם
    /// השירות יותקן מחדש: Tick הראשון קורא ל-EnforcePolicies(false) לפי touched ששמור ב-hardlock.json.
    /// </summary>
    private static void RestoreOrphanedPolicies()
    {
        try
        {
            if (ServiceInstalled()) return; // השירות קיים — הוא אחראי על הניקוי של עצמו
            if (Kiosk.IsActive) return; // נעילה רכה פעילה מגדירה DisableTaskMgr בעצמה — לא נוגעים
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", true);
            if (k == null) return;
            bool cleared = false;
            foreach (string name in new[] { "DisableTaskMgr", "DisableRegistryTools" })
                if (k.GetValue(name) is int v && v == 1) { k.DeleteValue(name, false); cleared = true; }
            if (cleared) Config.Log("hardlock: מדיניות נעילה יתומה (השירות הוסר בלי SelfUninstall) נוקתה בחשבון הנוכחי");
        }
        catch (Exception ex) { Config.Log("hardlock restore: " + ex.Message); }
    }

    // ================= התקנת השירות (רץ בהרשאות מנהל) =================

    /// <summary>
    /// תיקון שריד: אם ניסיון התקנה קודם נעצר באמצע (אחרי שהשירות כתב DisableTaskMgr וכו' לחשבון
    /// המשתמש, לפני שסיים), מפתח ה-Policies\System נשאר עם הרשאות SYSTEM/מנהל בלבד - גם המשתמש
    /// הבעלים שלו עצמו לא יכול לכתוב/לנקות אותו יותר. מתוקן כאן כי התהליך הזה כן מורם (runas) -
    /// מעניק בחזרה שליטה מלאה למשתמש הנוכחי (לא פותח יותר ממה שהיה ב-Windows רגיל ללא הקיוסק בכלל).
    /// </summary>
    private static void RepairUserPoliciesKeyAcl()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System",
                RegistryKeyPermissionCheck.ReadWriteSubTree, System.Security.AccessControl.RegistryRights.ChangePermissions | System.Security.AccessControl.RegistryRights.FullControl);
            if (k == null) return; // לא קיים כלל - שום ACL לתקן
            var acl = k.GetAccessControl();
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (user == null) return;
            acl.SetAccessRule(new System.Security.AccessControl.RegistryAccessRule(user, System.Security.AccessControl.RegistryRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.None, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            k.SetAccessControl(acl);
            SvcLog("install: הרשאות Policies\\System של המשתמש שוחזרו");
        }
        catch (Exception ex) { SvcLog("install: תיקון הרשאות Policies\\System נכשל (לא קריטי): " + ex.Message); }
    }

    public static int InstallService()
    {
        try
        {
            SecureDir(DataDir); // יוצר ומאבטח אטומית (בלי CreateDirectory קודם)
            SvcLog("install: מתחיל, גרסה " + Program.Version);
            RepairUserPoliciesKeyAcl();

            bool exists = ServiceInstalled();
            if (exists)
            {
                StopService();
                // הגשר שהפעיל אותנו (אם רץ מ-Program Files) לא נהרג — ה-exe שלו מוחלף בהעברה-הצידה (CopyVerified)
                string[] a0 = Environment.GetCommandLineArgs();
                int pi0 = Array.IndexOf(a0, "--parent-pid");
                int.TryParse(pi0 >= 0 && pi0 + 1 < a0.Length ? a0[pi0 + 1] : "", out int parentPid);
                StopBridgesAt(ServiceExe, parentPid);
            } // אחרת ההעתקה נכשלת על exe נעול (הגשר רץ מ-Program Files ומוגן מהריגה)

            Directory.CreateDirectory(ProgDir);
            // לא מעתיקים exe מנתיב שמשתמש רגיל כותב אליו בלי אימות גיבוב (ר' InstallTrust): אחרת החלפת הקובץ ב-LOCALAPPDATA
            // לפני ההעתקה הייתה מריצה קוד כ-SYSTEM. הבדיקה לפני עצירת השירות/שינוי כלשהו נוסף — כשל משאיר את המצב הקיים
            string[] cmd = Environment.GetCommandLineArgs();
            int hi = Array.IndexOf(cmd, "--src-sha256");
            string expectedHash = hi >= 0 && hi + 1 < cmd.Length ? cmd[hi + 1] : null;
            string copyErr = InstallTrust.CopyVerified(Environment.ProcessPath!, ServiceExe, expectedHash);
            if (copyErr != null)
            {
                SvcLog("install: " + copyErr);
                return 4;
            }
            RegisterMachineRun();

            string bin = "\"" + ServiceExe + "\" --service";
            if (!exists)
                Sc($"create {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto DisplayName= \"{Program.AppFullName}\"");
            else
                Sc($"config {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto DisplayName= \"{Program.AppFullName}\"");
            Sc($"description {ServiceName} \"{Program.AppFullName} — רכיב הנעילה המוקשחת\"");
            foreach (string a in ServiceRecovery.FailureArgs(ServiceName)) Sc(a);

            PrepareBundleDir();
            using var sc = new ServiceController(ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            SvcLog("install: השירות רץ");

            // התקנה ראשונה (ביקורת #30 ממצא 2 דורשת מנהל ב-op=setup): התהליך הזה כן מורם (runas),
            // אז מעבירים את הגיבובים מכאן ישירות, במקום להשאיר את זה לגשר הרגיל שקורא לנו אחר כך —
            // הוא לעולם לא מורם (רץ בהרשאות משתמש רגילות, בכוונה), אז הקריאה שלו הייתה נכשלת תמיד
            // עם "הגדרה ראשונה דורשת הרשאות מנהל" - גם בהתקנה לגיטימית לגמרי.
            JsonObject st = null;
            for (int i = 0; i < 20 && st == null; i++) { st = Call(new JsonObject { ["op"] = "status" }, 1000); if (st == null) Thread.Sleep(500); }
            if (st != null && st["configured"]?.GetValue<bool>() != true)
            {
                var setupRes = Call(new JsonObject
                {
                    ["op"] = "setup",
                    ["passwordHash"] = Config.Get<string>("kiosk.passwordHash", ""),
                    ["salt"] = Config.Get<string>("kiosk.salt", ""),
                    ["recoveryHash"] = Config.Get<string>("kiosk.recoveryHash", ""),
                    ["recoverySalt"] = Config.Get<string>("kiosk.recoverySalt", ""),
                    ["twoFactor"] = Config.Get("kiosk.twoFactor", false),
                });
                if (setupRes?["ok"]?.GetValue<bool>() != true)
                    SvcLog("install: setup נכשל: " + (setupRes?["message"]?.ToString() ?? "אין תשובה מהשירות"));
            }
            return 0;
        }
        catch (Exception ex)
        {
            SvcLog("install failed: " + ex);
            return 3;
        }
    }

    /// <summary>
    /// Run של HKLM (כל המשתמשים) מ-Program Files במקום Run של HKCU מ-%LOCALAPPDATA%. WHY: ה-exe ב-LOCALAPPDATA כתיב למשתמש —
    /// הוא יכול להחליף אותו, ו-Run של HKCU בכלל ניתן למחיקה. עכשיו הגשר עולה בכניסה מקובץ שרק מנהל כותב
    /// (וחשבונות Windows נוספים מקבלים אותו בלי התקנה לכל אחד). Installer.Install מסיר את הערך של HKCU כשזה קיים
    /// </summary>
    private static void RegisterMachineRun()
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(Installer.RunKey);
            k.SetValue(Installer.RunValue, Installer.MachineRunCommand);
            SvcLog("install: Run של HKLM הוגדר");
        }
        catch (Exception ex) { SvcLog("install: Run של HKLM נכשל (ממשיך עם HKCU): " + ex.Message); }
    }

    internal static void UnregisterMachineRun()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(Installer.RunKey, true); k?.DeleteValue(Installer.RunValue, false); }
        catch (Exception ex) { SvcLog("uninstall: מחיקת Run של HKLM: " + ex.Message); }
    }

    /// <summary>הורג תהליכי גשר שרצים מהנתיב הנתון (שירות/מורם בלבד — הם מוגנים מהריגה ע"י המשתמש)</summary>
    internal static void StopBridgesAt(string exe, int skipPid = 0)
    {
        try
        {
            foreach (var p in BridgeProcs.Scan().Where(x => x.Pid != skipPid && BridgeProcs.SamePath(x.Path, exe)))
                try { using var pr = Process.GetProcessById(p.Pid); pr.Kill(); pr.WaitForExit(3000); } catch { }
        }
        catch (Exception ex) { SvcLog("stop bridges: " + ex.Message); }
    }

    private static void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception ex) { SvcLog("stop: " + ex.Message); }
        Thread.Sleep(500);
    }

    private static void Sc(string args)
    {
        string outp = Config.Run("sc.exe", args, 20000);
        SvcLog("sc " + args.Split(' ')[0] + ": " + outp.Trim().Replace("\r\n", " "));
    }

    /// <summary>ה-ACL של DataDir: SYSTEM ומנהלים בלבד, בעלים = מנהלים, בלי ירושה</summary>
    internal static DirectorySecurity BuildDirSecurity()
    {
        var ds = new DirectorySecurity();
        ds.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        ds.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return ds;
    }

    /// <summary>בעלים מהימן של DataDir: SYSTEM או מנהלים. כל בעלים אחר יכול לשנות הרשאות</summary>
    internal static bool IsTrustedOwner(SecurityIdentifier owner) =>
        owner != null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    /// <summary>
    /// התיקייה: SYSTEM ומנהלים בלבד. WHY (S1 ממצא 7): קודם delete+create+set השאיר חלון שבו התיקייה החדשה ירשה הרשאות של ProgramData
    /// (משתמשים יוצרים/כותבים) ותוקף יכול היה לזרוע בה קבצים/junction. עכשיו היא נוצרת עם ה-DACL כבר ברגע היצירה (Create(DirectorySecurity)),
    /// תיקייה קיימת עם בעלים לא מהימן או reparse מוזזת הצידה, ואחרי היצירה מאמתים שוב (מרוץ: תוקף שיצר אותה מחדש) בלולאה חסומה.
    /// </summary>
    private static void SecureDir(string dir)
    {
        var ds = BuildDirSecurity();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    bool bad = IsReparse(dir);
                    if (!bad)
                    {
                        var owner = new DirectoryInfo(dir).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                        bad = !IsTrustedOwner(owner);
                    }
                    if (bad)
                    {
                        // reparse: מסירים את הקישור עצמו (לא נוגעים ביעד). תיקייה זרה: מזיזים הצידה (לא רקורסיבית דרך junction פנימי), ואם לא הצליח מוחקים
                        if (IsReparse(dir)) Directory.Delete(dir, false);
                        else
                        {
                            try { Directory.Move(dir, dir + ".untrusted-" + Guid.NewGuid().ToString("N")); }
                            catch { Directory.Delete(dir, true); }
                        }
                        SvcLog("secure-dir: תיקייה לא מהימנה הוסרה/הוזזה: " + dir);
                    }
                }
                if (!Directory.Exists(dir)) new DirectoryInfo(dir).Create(ds); // ה-DACL נקבע אטומית ביצירה
                // אימות אחרי היצירה: אם תוקף הקדים ויצר אותה, הלולאה תחזור ותזיז אותה
                var after = new DirectoryInfo(dir).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (IsReparse(dir) || !IsTrustedOwner(after)) continue;
                new DirectoryInfo(dir).SetAccessControl(ds); // idempotent: מיישר גם תיקייה קיימת מהימנה
                return;
            }
            catch (Exception ex) { SvcLog("secure-dir: " + ex.Message); }
        }
        throw new InvalidOperationException("לא ניתן לאבטח את תיקיית הנתונים " + dir);
    }

    /// <summary>האם התיקיה היא reparse point (junction/symlink)</summary>
    internal static bool IsReparse(string dir)
    {
        try { return Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    // תיקיית חילוץ הספריות הנייטיב (single-file) שהשירות מחלץ אליה ושממנה הגשר טוען — כתיבה רק ל-SYSTEM/מנהלים, קריאה/הרצה לכולם.
    // מוכנה רק כשהשירות עצמו חילץ אליה (ר' MarkBundleReady); אחרת הגשר ממשיך בברירת המחדל (אין רגרסיה)
    public static string BundleDir => Path.Combine(DataDir, "net");
    internal static volatile bool BundleReady;

    /// <summary>התקנה (מורמת): יוצר את תיקיית החילוץ עם ACL וקובע אותה כמשתנה סביבה של השירות (REG_MULTI_SZ), לפני הפעלתו</summary>
    private static void PrepareBundleDir()
    {
        try
        {
            if (IsReparse(BundleDir)) Directory.Delete(BundleDir, false);
            Directory.CreateDirectory(BundleDir);
            var ds = new DirectorySecurity();
            ds.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            ds.SetAccessRuleProtection(true, false);
            var inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var w in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(w, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(BundleDir).SetAccessControl(ds);
            using var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            k.SetValue("Environment", new[] { "DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + BundleDir }, RegistryValueKind.MultiString);
            SvcLog("install: תיקיית חילוץ מוגנת הוכנה (" + BundleDir + ")");
        }
        catch (Exception ex) { SvcLog("install: הכנת תיקיית חילוץ נכשלה (לא קריטי): " + ex.Message); }
    }

    /// <summary>השירות בעלייה: אם הוא עצמו רץ עם תיקיית החילוץ המוגנת ויש בה קבצים — גשרים שיופעלו יקבלו אותה (BundleReady)</summary>
    internal static void MarkBundleReady()
    {
        try
        {
            string env = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
            BundleReady = env != null && SamePathDir(env, BundleDir) && !IsReparse(BundleDir)
                          && Directory.EnumerateFiles(BundleDir, "*", SearchOption.AllDirectories).Any();
        }
        catch { BundleReady = false; }
        SvcLog("service: תיקיית חילוץ מוגנת " + (BundleReady ? "מוכנה" : "לא פעילה (ברירת מחדל)"));
    }

    private static bool SamePathDir(string a, string b) => BridgeProcs.SamePath(a.TrimEnd('\\'), b.TrimEnd('\\'));

    private static readonly LogThrottle SvcLogThrottle = new();

    internal static void SvcLog(string line)
    {
        try
        {
            // WHY (U1): בעבר הקובץ נמחק מעל 2MB (איבוד ראיות), וקריאות פגומות חוזרות הציפו אותו. עכשיו: סיבוב ל-.1 והגבלת קצב/כפילויות
            int dropped;
            lock (SvcLogThrottle) if (!SvcLogThrottle.Allow(line, Environment.TickCount64, out dropped)) return;
            Directory.CreateDirectory(DataDir);
            string f = Path.Combine(DataDir, "service.log");
            LogRotator.RotateIfLarge(f, 2_000_000);
            string pre = dropped > 0 ? DateTime.Now.ToString("s") + " (" + dropped + " שורות דומות הושמטו)" + Environment.NewLine : "";
            File.AppendAllText(f, pre + DateTime.Now.ToString("s") + " " + line + Environment.NewLine);
        }
        catch { }
    }
}

/// <summary>השירות עצמו (LocalSystem). מופעל עם OtzariaBridge.exe --service</summary>
public class HardLockService : ServiceBase
{
    private const string PoliciesSub = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string UsbStorKey = @"SYSTEM\CurrentControlSet\Services\USBSTOR";
    private static readonly Regex UserSid = new(@"^S-1-5-21-[\d-]+$", RegexOptions.Compiled);

    private readonly object _gate = new();
    private JsonObject _state;
    // S1 ממצא 6: קובץ המצב קיים אך פגום (ואין גיבוי תקין) => fail-closed: נחשב מוגדר ונעול, שום סיסמה לא עובדת, ורק מנהל מורם יכול להסיר/להגדיר מחדש
    private volatile bool _corrupt;
    private volatile bool _uninstalling; // S1 ממצא 3: מפסיק את אכיפת האתחול בהסרה מלאה
    private System.Threading.Timer _threadTimer;
    private readonly object _threadGate = new();
    private volatile HashSet<int> _guardPids = new(); // PIDs של גשרים אמיתיים (נתיב = ServiceExe) לחוטים; מוחלף כולו (copy-on-write)
    private volatile HashSet<int> _ownSnapshot = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _policyIssues = new(); // sid -> סיבה; ממצא גלוי בסטטוס
    private System.Threading.Timer _timer;
    private CancellationTokenSource _cts;
    // הגבלת ניסיונות סיסמה לפי לקוח (SID + מורם), לא גלובלית — ר' AttemptLimiter. נקרא תחת _gate
    private readonly AttemptLimiter _limiter = new();
    [ThreadStatic] private static string _clientKey;
    private static long NowMs => Environment.TickCount64;

    private readonly Dictionary<int, DateTime> _sessionSeen = new();
    private readonly Dictionary<int, DateTime> _lastLaunch = new();
    private readonly Dictionary<int, int> _launchStreak = new(); // הפעלות רצופות מאז שהגשר נראה רץ בסשן
    private readonly object _bridgeGate = new();
    private System.Threading.Timer _bridgeTimer;
    private int _sessions;

    // פתיחה זמנית: סיום מונוטוני בזיכרון בלבד (0 = אין). לא נשמר בדיסק — אחרי אתחול/הפעלה מחדש של השירות הנעילה חוזרת
    private long _tempEndTick;
    private readonly ClockGuard _clock = new();

    // חיבורי pipe: מופעי האזנה, ומגבלת חיבורים במקביל (סך הכול / לפי PID)
    private const int PipeListeners = 4, PipeMaxInstances = 32;
    private readonly ConnLimiter _conns = new(reservedElevated: 6); // 24 סך הכול, 6 מהם שמורים למורמים
    private int _firstPipe;
    // סיסמת פתיחה זמנית: backoff אקספוננציאלי עד שעה (נפרד מדלי הסיסמה הראשית)
    private readonly AttemptLimiter _tempLimiter = new(maxFailures: 3, lockSeconds: 30, forgetMinutes: 60, maxLockSeconds: 3600);
    // גשרים שהשירות יצר בעצמו (pid -> זמן התחלה): רק הם נחשבים מהימנים; כל גשר אמיתי אחר נהרג כשהנעילה נאכפת
    private readonly Dictionary<int, DateTime> _ownBridges = new();

    private readonly RecoveryTokens _recoveryTokens = new(); // טוקני שחרור חירום חד-פעמיים (זיכרון בלבד; נקרא תחת _gate)
    private byte[] _sealKey; // מפתח HMAC של ConfigSeal, נטען עצלנית (נקרא תחת _gate)
    private static string StateFile => Path.Combine(HardLock.DataDir, "hardlock.json");

    public HardLockService()
    {
        ServiceName = HardLock.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true; // SERVICE_CONTROL_SESSIONCHANGE: הגשר עולה מיד בכניסה/ביטול נעילת מסך (T1)
    }

    // סשנים שנכנסו/ננעלו-נפתחו: הגשר שלהם מופעל בלי חסד הסשן (ר' RelaunchPolicy.ShouldLaunch immediate)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _immediateSessions = new();

    /// <summary>
    /// WHY (T1): אחרי Sign out/כניסה מחדש הסשן חדש והגשר לא רץ בו; בלי אירוע זה ההפעלה מחכה לחסד הסשן (3 שניות) ולטיימר של שנייה.
    /// כאן מסמנים את הסשן כ"מיידי" ומפעילים סבב מיידי, אם הנעילה נאכפת. הסבב עצמו רץ ברקע כדי לא לחסום את תור אירועי ה-SCM
    /// </summary>
    protected override void OnSessionChange(SessionChangeDescription change)
    {
        try
        {
            if (!SessionTrigger.IsLogonLike(change.Reason) || change.SessionId <= 0) return;
            _immediateSessions[change.SessionId] = 0;
            Task.Run(() =>
            {
                if (!Monitor.TryEnter(_bridgeGate, 3000)) return;
                try
                {
                    bool enforced;
                    lock (_gate) enforced = Configured && Enforced;
                    if (enforced) EnsureBridgeInSessions();
                }
                catch (Exception ex) { HardLock.SvcLog("session change: " + ex.Message); }
                finally { Monitor.Exit(_bridgeGate); }
            });
        }
        catch (Exception ex) { HardLock.SvcLog("session change: " + ex.Message); }
    }

    protected override void OnStart(string[] args)
    {
        Load();
        HardLock.MarkBundleReady();
        // פתיחה זמנית לא שורדת אתחול/הפעלה מחדש: אין לנו נקודת סיום מונוטונית אמינה מעבר לתהליך הזה, ושעון הקיר ניתן להזזה
        if (!string.IsNullOrEmpty(_state["tempUnlockUntil"]?.ToString()))
        {
            _state["tempUnlockUntil"] = "";
            try { Save(); } catch (Exception ex) { HardLock.SvcLog("load: ניקוי פתיחה זמנית: " + ex.Message); }
            HardLock.SvcLog("service: פתיחה זמנית שנשארה מלפני הפעלת השירות נוקתה");
        }
        _cts = new CancellationTokenSource();
        for (int i = 0; i < PipeListeners; i++) Task.Run(() => PipeLoop(_cts.Token));
        _timer = new System.Threading.Timer(_ => Tick(), null, 2000, 5000);
        _bridgeTimer = new System.Threading.Timer(_ => WatchBridge(), null, 1000, 1000);
        _threadTimer = new System.Threading.Timer(_ => ThreadGuardTick(), null, 200, ProcessDacl.ThreadHardenIntervalMs);
        HardLock.SvcLog("service: עלה (גרסה " + Program.Version + "), נעילה " + (Active ? "פעילה" : "כבויה"));
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _bridgeTimer?.Dispose();
        _threadTimer?.Dispose();
        // עצירה יזומה של השירות (sc stop): אף אחד אחר כבר לא יאכוף, ו-HKEY_USERS של המשתמשים נשאר עם
        // DisableTaskMgr/DisableRegistryTools (וחסימת USB) - מנהל המשימות ועורך הרישום חסומים בלי שום נעילה.
        // מנקים בעצירה. הנעילה השמורה (active) לא משתנה, ולכן כשהשירות יעלה שוב ה-Tick הראשון יאכוף מחדש
        // S1 ממצא 3: הקשחת האתחול (SafeBoot/WinRE/bcd) לא משתחררת בעצירה; רק בהסרה מלאה (SelfUninstall)
        try { EnforcePolicies(false); EnforceUsb(false); EnforceOffice(false); }
        catch (Exception ex) { HardLock.SvcLog("stop cleanup: " + ex.Message); }
        HardLock.SvcLog("service: נעצר");
    }

    // כיבוי מחשב: לא מנקים - הנעילה חייבת להישאר בתוקף לעלייה הבאה (השירות עולה אוטומטית ואוכף מחדש)
    protected override void OnShutdown()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _bridgeTimer?.Dispose();
        _threadTimer?.Dispose();
        HardLock.SvcLog("service: כיבוי מחשב");
    }

    // ---------- מצב ----------

    /// <summary>הנעילה "דלוקה" (ידנית או בתזמון). האכיפה בפועל — Enforced, שמתחשב גם בפתיחה זמנית</summary>
    private bool Active => _corrupt || _state["active"]?.GetValue<bool>() == true;
    private bool Configured => _corrupt || !string.IsNullOrEmpty(_state["passwordHash"]?.ToString());

    // ---------- פתיחה זמנית: השירות הוא מקור האמת לסוד ולזמן הסיום ----------
    // tempUnlockUntil נשמר רק כאן (hardlock.json, כתיב רק ל-SYSTEM ולמנהלים), ולכן עריכת config.json לא מאריכה.
    // הנעילה עצמה (active/activatedBy) לא משתנה בזמן הפתיחה; רק האכיפה מושהית. כך בתום הזמן הנעילה חוזרת
    // מעצמה, ונעילה שהתזמון הפעיל וחלונה הסתיים בינתיים — לא חוזרת (EvaluateSchedule ממשיך לרוץ כרגיל).
    // רדומה כשהאימות הדו-שלבי כבוי: אי אפשר להשתמש בה, ופתיחה שרצה מסתיימת (ראו ParseTempUnlock).

    private bool TempUnlockUsable =>
        _state["tempUnlockEnabled"]?.GetValue<bool>() == true && TwoFactorOn && !string.IsNullOrEmpty(_state["tempUnlockHash"]?.ToString());

    private int TempUnlockMinutes =>
        TempUnlock.ClampMinutes(_state["tempUnlockMinutes"] is JsonValue mv && mv.TryGetValue<int>(out int m) ? m : TempUnlock.DefaultMinutes);

    // נקודת הסיום המונוטונית (_tempEndTick) היא הקובעת, לא שעון הקיר: הזזת שעון/אזור זמן לא מאריכה ולא מקצרת. הערך השמור
    // (tempUnlockUntil) משמש לתצוגה ולסימון "יש פתיחה" בלבד — מחיקתו מסיימת את הפתיחה
    private int TempSecondsLeft =>
        TempUnlockUsable && !string.IsNullOrEmpty(_state["tempUnlockUntil"]?.ToString())
            ? TempUnlock.MonoSecondsLeft(_tempEndTick, NowMs) : 0;

    /// <summary>האם הנעילה נאכפת עכשיו: דלוקה, ולא בתוך פתיחה זמנית</summary>
    private bool Enforced => Active && TempSecondsLeft == 0;

    private JsonObject TempNode()
    {
        int left = TempSecondsLeft;
        return new JsonObject
        {
            ["enabled"] = _state["tempUnlockEnabled"]?.GetValue<bool>() == true,
            ["configured"] = !string.IsNullOrEmpty(_state["tempUnlockHash"]?.ToString()),
            ["minutes"] = TempUnlockMinutes,
            ["until"] = left > 0 ? _state["tempUnlockUntil"]?.ToString() ?? "" : "",
            ["secondsLeft"] = left,
        };
    }

    /// <summary>
    /// מאמת את שדות הפתיחה הזמנית שבבקשת "settings" (לפני כל כתיבה). wantTf = הערך החדש של האימות הדו-שלבי אם נשלח.
    /// הפעלה (מכבוי לדולק) בלי אימות דו-שלבי נדחית; מתג שכבר דלוק נשמר (רדום). כשהאימות הדו-שלבי כבוי או
    /// שהפתיחה כובתה — פתיחה שרצה מסתיימת מיד.
    /// </summary>
    private JsonObject ParseTempUnlock(JsonObject q, bool? wantTf, out Dictionary<string, JsonNode> res)
    {
        res = new Dictionary<string, JsonNode>();
        bool tfAfter = wantTf ?? TwoFactorOn;
        bool was = _state["tempUnlockEnabled"]?.GetValue<bool>() == true;
        bool? want = null;
        if (q["tempUnlockEnabled"] is JsonNode en)
        {
            if (en is not JsonValue ev || !ev.TryGetValue<bool>(out bool b)) return Err("bad_request", "tempUnlockEnabled אינו תקין");
            want = b;
        }
        if (q["tempUnlockMinutes"] is JsonNode mn)
        {
            if (mn is not JsonValue mv || !mv.TryGetValue<int>(out int m)) return Err("bad_request", "tempUnlockMinutes אינו תקין");
            res["tempUnlockMinutes"] = TempUnlock.ClampMinutes(m);
        }
        string hash = q["tempUnlockHash"]?.ToString(), salt = q["tempUnlockSalt"]?.ToString();
        if (!string.IsNullOrEmpty(hash) || !string.IsNullOrEmpty(salt))
        {
            static bool B64(string s, int len) { try { return Convert.FromBase64String(s ?? "").Length == len; } catch { return false; } }
            // (ההשוואה לסיסמת הנעילה נעשית בגשר, שמקבל את הטקסט הגלוי; כאן מגיע רק גיבוב עם salt משלו)
            if (!B64(hash, 32) || !B64(salt, 16)) return Err("bad_request", "סיסמת הפתיחה הזמנית אינה תקינה");
            res["tempUnlockHash"] = hash;
            res["tempUnlockSalt"] = salt;
        }
        bool configured = res.ContainsKey("tempUnlockHash") || !string.IsNullOrEmpty(_state["tempUnlockHash"]?.ToString());
        if (want == true && !was)
        {
            if (!tfAfter) return Err("bad_request", "פתיחה זמנית דורשת אימות דו-שלבי (Windows Hello) פעיל. הפעילו קודם את האימות הדו-שלבי");
            if (!configured) return Err("bad_request", "יש לבחור סיסמת פתיחה זמנית לפני ההפעלה");
        }
        if (want != null) res["tempUnlockEnabled"] = want.Value;
        // רדומה: אימות דו-שלבי כבוי או מתג כבוי → פתיחה שרצה מסתיימת, והנעילה חוזרת בטיק הבא
        if ((!tfAfter || want == false) && !string.IsNullOrEmpty(_state["tempUnlockUntil"]?.ToString())) res["tempUnlockUntil"] = "";
        return null;
    }

    private void Load()
    {
        _state = StateStore.Read(StateFile, out var st, out bool restored);
        _corrupt = st == StateLoad.Corrupt;
        if (_corrupt)
        {
            // לא דורסים ולא מוחקים: משמרים עותק לבדיקה. נשארים נעולים (Configured+Active) עד שמנהל מורם מגדיר מחדש/מסיר
            try { if (File.Exists(StateFile)) File.Copy(StateFile, StateFile + ".corrupt", true); } catch { }
            HardLock.SvcLog("load: קובץ המצב פגום ואין גיבוי תקין. נשארים נעולים (fail-closed); נדרש מנהל מורם (setup/uninstall)");
        }
        else if (restored)
        {
            HardLock.SvcLog("load: קובץ המצב הראשי חסר/פגום; שוחזר מהגיבוי");
            _state ??= new JsonObject();
            try { Save(); } catch (Exception ex) { HardLock.SvcLog("load: כתיבת השחזור: " + ex.Message); }
        }
        _state ??= new JsonObject();
    }

    // TypeInfoResolver מפורש הכרחי כאן (לא רק WriteIndented): נצפה בפועל ש-JsonNode.ToJsonString(options)
    // עם JsonSerializerOptions "ריקות" זורק "JsonSerializerOptions instance must specify a TypeInfoResolver"
    // בתוך תהליך השירות (HardLockService) - ללא זה, כל שמירת מצב (כולל disable/enable) נכשלת בפועל
    private static readonly JsonSerializerOptions SaveOptions =
        new() { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    private void Save()
    {
        // במצב פגום לא כותבים (לא דורסים את הראיה/הגיבוי); setup של מנהל מורם מנקה את הדגל לפני Save
        if (_corrupt) return;
        // אטומי: זמני + Flush(true) + גיבוי של התקין האחרון + החלפה (ר' StateStore)
        StateStore.WriteAtomic(StateFile, _state.ToJsonString(SaveOptions));
    }

    // החישוב הקריפטוגרפי הטהור (Hash/Matches/נורמליזציה) משותף עם Kiosk דרך Secret.cs

    private bool Matches(string secret, string hashKey, string saltKey) =>
        Secret.Matches(secret, _state[hashKey]?.ToString(), _state[saltKey]?.ToString());

    /// <summary>
    /// WHY (T1): גיבוב ישן (200k איטרציות, בלי תחילית v2) משודרג לפורמט הנוכחי בכניסה מוצלחת, כשהסוד הגלוי בידינו. אותו salt.
    /// כשל שמירה לא פוסל את האימות (הישן עדיין תקף). נקרא תחת _gate
    /// </summary>
    private void UpgradeHash(string secret, string hashKey, string saltKey)
    {
        try
        {
            if (!Secret.NeedsUpgrade(_state[hashKey]?.ToString())) return;
            _state[hashKey] = Secret.Hash(secret, Convert.FromBase64String(_state[saltKey]?.ToString() ?? ""));
            Save();
            HardLock.SvcLog("service: גיבוב " + hashKey + " שודרג לפורמט הנוכחי");
        }
        catch (Exception ex) { HardLock.SvcLog("upgrade hash: " + ex.Message); }
    }

    /// <summary>null = תקין; אחרת תשובת שגיאה</summary>
    private JsonObject Check(string password, bool allowRecovery)
    {
        if (_corrupt) return Err("corrupt", "קובץ המצב של רכיב ההקשחה פגום והמערכת נשארת נעולה. נדרש מנהל מורם להגדרה מחדש/הסרה");
        if (!Configured) return Err("bad_request", "רכיב ההקשחה עדיין לא הוגדר");
        if (!_limiter.CanTry(_clientKey, NowMs, out int wait))
            return Err("too_many", $"יותר מדי ניסיונות שגויים. נסו שוב בעוד {wait} שניות");
        bool ok = false;
        if (Matches(password, "passwordHash", "salt")) { ok = true; UpgradeHash(password, "passwordHash", "salt"); }
        else if (allowRecovery && Matches(Secret.NormalizeRecovery(password), "recoveryHash", "recoverySalt"))
        { ok = true; UpgradeHash(Secret.NormalizeRecovery(password), "recoveryHash", "recoverySalt"); }
        if (ok) { _limiter.Success(_clientKey); return null; }
        _limiter.Fail(_clientKey, NowMs);
        return Err("wrong_password", "סיסמה שגויה");
    }

    private static JsonObject Err(string code, string message) => new() { ["ok"] = false, ["code"] = code, ["message"] = message };
    private static JsonObject Ok() => new() { ["ok"] = true };

    // ---------- מדיניות הנעילה (מקור האמת כשהשירות מותקן) ----------

    private static readonly string[] PolicyKeys = { "programs", "autoTorahPrograms", "autoTorahKeywords", "otzariaPath", "otzariaSha256", "fxPolicy" };

    /// <summary>
    /// נתיב אוצריא שמגיע עם "enable". WHY (U1): "enable" עקף את ParsePolicy (נתיב לא תקין/מעטפת/מנהל מערכת נכנסו ישירות למצב), ונתיב שהשתנה השאיר נעיצת
    /// SHA-256 של הקובץ הקודם. עכשיו: אותה בדיקה כמו "settings", ונעיצה ישנה מנוקה כשהנתיב שונה (ParsePolicy מנקה אותה); נתיב זהה משאיר את הנעיצה.
    /// מחזיר שגיאה (בלי לשנות את state) או null
    /// </summary>
    internal static JsonObject ApplyEnableOtzariaPath(JsonObject state, string otz)
    {
        if (string.IsNullOrEmpty(otz)) return null;
        var e = ParsePolicy(new JsonObject { ["otzariaPath"] = otz }, out var enPolicy);
        if (e != null) return e;
        bool samePath = BridgeProcs.SamePath(state["otzariaPath"]?.ToString(), otz);
        foreach (var kv in enPolicy)
            if (!(samePath && kv.Key == "otzariaSha256")) state[kv.Key] = kv.Value;
        return null;
    }

    /// <summary>המדיניות השמורה, לתשובת "status". רק מפתחות שהוגדרו — מפתח חסר = עוד לא סונכרן</summary>
    private JsonObject PolicyNode()
    {
        var o = new JsonObject();
        foreach (string k in PolicyKeys)
            if (_state[k] is JsonNode n) o[k] = n.DeepClone();
        return o;
    }

    /// <summary>
    /// מאמת את שדות המדיניות שבבקשת "settings" ומחזיר אותם בצורה נקייה. אותן מגבלות כמו בגשר:
    /// עד 20 תוכנות, נתיב exe מלא, בלי מעטפות/מארחי סקריפטים/מנהלי מערכת (גם לא כ"אוצריא").
    /// </summary>
    private static JsonObject ParsePolicy(JsonObject q, out Dictionary<string, JsonNode> res)
    {
        res = new Dictionary<string, JsonNode>();
        static bool ExePath(string p) =>
            Programs.IsShellApp(p) || // אפליקציית חנות (shell:AppsFolder\...) - בלי exe, מזהה מאומת בנפרד
            (Path.IsPathFullyQualified(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !Programs.IsBlockedName(Path.GetFileNameWithoutExtension(p)));

        if (q["programs"] is JsonNode pn)
        {
            if (pn is not JsonArray pa || pa.Count > Programs.MaxPrograms) return Err("bad_request", "רשימת התוכנות אינה תקינה");
            var clean = new JsonArray();
            foreach (var item in pa)
            {
                if (item is not JsonObject o) return Err("bad_request", "רשומת תוכנה אינה תקינה");
                string id = o["id"]?.ToString() ?? "", name = o["name"]?.ToString() ?? "", path = (o["path"]?.ToString() ?? "").Trim();
                if (id.Length is 0 or > 40 || name.Length > 60 || !ExePath(path))
                    return Err("bad_request", "רשומת תוכנה אינה תקינה: " + (name.Length > 0 ? name : id));
                var procs = new JsonArray();
                if (o["procs"] is JsonArray pr)
                    foreach (var x in pr)
                    {
                        string n = x?.ToString() ?? "";
                        if (!Programs.IsValidProcName(n)) return Err("bad_request", "שם תהליך אינו תקין: " + n);
                        procs.Add(n);
                    }
                // [w3] נעיצת זהות (SHA-256 / מפרסם Authenticode) חייבת לשרוד את אחסון המדיניות בשירות
                string sha = (o["sha256"]?.ToString() ?? "").Trim().ToLowerInvariant(), signer = (o["signer"]?.ToString() ?? "").Trim();
                if (sha.Length != 0 && !System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{64}$")) return Err("bad_request", "SHA-256 אינו תקין: " + name);
                if (signer.Length > 400) return Err("bad_request", "שם מפרסם ארוך מדי: " + name);
                clean.Add(new JsonObject { ["id"] = id, ["name"] = name.Length > 0 ? name : id, ["path"] = path, ["procs"] = procs, ["sha256"] = sha, ["signer"] = signer });
            }
            res["programs"] = clean;
        }
        if (q["autoTorahPrograms"] is JsonValue at) res["autoTorahPrograms"] = at.GetValue<bool>();
        if (q["autoTorahKeywords"] is JsonNode kn)
        {
            if (kn is not JsonArray ka || ka.Count > 50) return Err("bad_request", "רשימת מילות המפתח אינה תקינה");
            var clean = new JsonArray();
            foreach (var x in ka)
            {
                string k = (x?.ToString() ?? "").Trim();
                if (k.Length > 60) return Err("bad_request", "מילת מפתח ארוכה מדי");
                if (k.Length > 0) clean.Add(k);
            }
            res["autoTorahKeywords"] = clean;
        }
        if (q.ContainsKey("otzariaPath"))
        {
            string op = (q["otzariaPath"]?.ToString() ?? "").Trim();
            if (op.Length > 0 && !ExePath(op)) return Err("bad_request", "נתיב אוצריא אינו תקין (נדרש נתיב מלא לקובץ exe)");
            res["otzariaPath"] = op;
            // נתיב חדש בלי נעיצה מפורשת: מנקים נעיצה ישנה (שייכת לקובץ אחר) — fail-closed
            if (!q.ContainsKey("otzariaSha256")) res["otzariaSha256"] = "";
        }
        if (q.ContainsKey("otzariaSha256"))
        {
            string osha = (q["otzariaSha256"]?.ToString() ?? "").Trim().ToLowerInvariant();
            if (osha.Length != 0 && !System.Text.RegularExpressions.Regex.IsMatch(osha, "^[0-9a-f]{64}$")) return Err("bad_request", "SHA-256 של אוצריא אינו תקין");
            res["otzariaSha256"] = osha;
        }
        if (q["fxPolicy"] is JsonNode fn)
        {
            if (fn is not JsonObject fo) return Err("bad_request", "מדיניות סייר הקבצים אינה תקינה");
            string mode = (fo["mode"]?.ToString() ?? "").ToLowerInvariant();
            if (mode is not ("off" or "restricted" or "locked")) return Err("bad_request", "mode חייב להיות off, restricted או locked");
            var roots = new JsonArray();
            if (fo["roots"] is JsonArray ra)
            {
                if (ra.Count > 32) return Err("bad_request", "יותר מדי תיקיות");
                foreach (var x in ra)
                {
                    string r = x?.ToString() ?? "";
                    if (!Path.IsPathFullyQualified(r)) return Err("bad_request", "תיקייה אינה תקינה: " + r);
                    roots.Add(r);
                }
            }
            int minutes = fo["unlockMinutes"] is JsonValue mv && mv.TryGetValue<int>(out int m) ? Math.Clamp(m, 1, 1440) : 15;
            res["fxPolicy"] = new JsonObject { ["mode"] = mode, ["roots"] = roots, ["unlockMinutes"] = minutes };
        }
        return null;
    }

    // ---------- Named Pipe ----------

    private static PipeSecurity MakePipeSecurity()
    {
        var ps = new PipeSecurity();
        // חיבור מרוחק (SMB) נדחה במפורש; הגשר והמשתמשים המקומיים בלבד
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }

    private async Task PipeLoop(CancellationToken ct)
    {
        var ps = MakePipeSecurity();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // כמה מאזינים במקביל ומופעים מרביים גבוהים (היה 3 לולאות / 8 מופעים): לקוח תוקע לא ממלא את כל המופעים וגורם
                // ל-"pipe לא עונה" אצל הגשר
                // FirstPipeInstance על המופע הראשון בלבד: אם תהליך משתמש כבר יצר pipe בשם הזה (squat) היצירה נכשלת ונרשמת ביומן, במקום
                // שהשירות ישתתף בשרשור מופעים עם תוקף. (הלקוח מאמת בנוסף את תהליך השרת — PipePeer)
                var opts = PipeOptions.Asynchronous;
                if (Interlocked.CompareExchange(ref _firstPipe, 1, 0) == 0) opts |= PipeOptions.FirstPipeInstance;
                NamedPipeServerStream pipe;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(HardLock.PipeName, PipeDirection.InOut, PipeMaxInstances,
                        PipeTransmissionMode.Byte, opts, 4096, 4096, ps);
                }
                catch { if ((opts & PipeOptions.FirstPipeInstance) != 0) { Interlocked.Exchange(ref _firstPipe, 0); HardLock.SvcLog("pipe: יצירת המופע הראשון נכשלה (ייתכן ששם ה-pipe תפוס בידי תהליך אחר)"); } throw; }
                try { await pipe.WaitForConnectionAsync(ct); }
                catch { pipe.Dispose(); throw; }
                // מגבלת חיבורים במקביל (סך הכול ולפי PID): מעבר לה החיבור נסגר מיד, בלי לתפוס חוט/מופע
                int cpid = 0;
                try { if (GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint u)) cpid = (int)u; } catch { }
                var (csid, celev) = ClientIdentity(cpid);
                if (!_conns.TryEnter(cpid, csid, celev)) { pipe.Dispose(); continue; }
                // ביקורת #30 ממצא 5: כל חיבור מטופל בנפרד, עם מגבלת אורך ודדליין, כך שלקוח תקוע
                // לא חוסם את הבאים אחריו (ולא דוחף את הגשר ל-fallback של config.json)
                _ = Task.Run(async () => { try { await ServeOne(pipe, ct); } finally { _conns.Leave(cpid, csid); } });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { HardLock.SvcLog("pipe loop: " + ex.Message); await Task.Delay(1000); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);

    /// <summary>SID ומורם של תהליך הלקוח (לפי הטוקן שלו), למכסות החיבורים. כשל → (null, false): נספר רק לפי PID וסך הכול</summary>
    private static (string sid, bool elevated) ClientIdentity(int pid)
    {
        IntPtr h = IntPtr.Zero, tok = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero || !OpenProcessToken(h, 0x8 /*TOKEN_QUERY*/, out tok)) return (null, false);
            buf = Marshal.AllocHGlobal(512);
            string sid = null; bool elev = false;
            if (GetTokenInformation(tok, 1 /*TokenUser*/, buf, 512, out _))
                sid = new SecurityIdentifier(Marshal.ReadIntPtr(buf)).Value;
            if (GetTokenInformation(tok, 20 /*TokenElevation*/, buf, 512, out _)) elev = Marshal.ReadInt32(buf) != 0;
            return (sid, elev);
        }
        catch { return (null, false); }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (tok != IntPtr.Zero) CloseHandle(tok);
            if (h != IntPtr.Zero) CloseHandle(h);
        }
    }

    private const int PipeMaxLine = 64 * 1024;
    [ThreadStatic] private static bool _clientAdmin;
    [ThreadStatic] private static int _clientPid;
    [ThreadStatic] private static int _clientSession;
    [ThreadStatic] private static bool _clientOwnBridge;

    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    // ---------- אימות דו-שלבי (Windows Hello) שנאכף בשירות ----------
    // השירות (SYSTEM) לא יכול להציג Hello בעצמו (סשן 0), ולכן הוא מפעיל בסשן של המשתמש תהליך עזר
    // (--hello-helper, מתוך הקובץ המותקן ב-Program Files). התהליך מדווח "hello-done" בצינור, והשירות
    // מקבל דיווח רק מה-PID שהוא עצמו הפעיל — כך לקוח צינור גולמי (סקריפט שיודע את הסיסמה) לא יכול
    // לדווח "אושר" בעצמו ולדלג על Hello.
    // מגבלה: תהליך העזר רץ בטוקן של המשתמש; תוקף שמזריק קוד לתוכו בחלון הזמן הקצר שהוא חי עדיין יכול
    // לזייף אישור. זו הגנה משמעותית מעבר לעקיפה טריוויאלית מול הצינור, לא הגנה מוחלטת.
    private sealed class HelloReq { public int Pid; public DateTime Expires; public DateTime ApprovedUntil; public string State = "pending"; }
    private readonly Dictionary<string, HelloReq> _hello = new();
    private readonly object _helloGate = new();
    private bool TwoFactorOn => _state["twoFactor"]?.GetValue<bool>() == true;

    private void PruneHello()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _hello.Where(kv => now > kv.Value.Expires && now > kv.Value.ApprovedUntil).Select(kv => kv.Key).ToList())
            _hello.Remove(k);
    }

    /// <summary>null = מותר להמשיך. כש-2FA פעיל (או force) נדרש אישור Hello חד-פעמי; קוד שחזור פטור (ביטול חירום)</summary>
    private JsonObject Need2fa(JsonObject q, string supplied, bool force = false)
    {
        if (!TwoFactorOn && !force) return null;
        if (!force && !string.IsNullOrEmpty(supplied) && !Matches(supplied, "passwordHash", "salt")
            && Matches(Secret.NormalizeRecovery(supplied), "recoveryHash", "recoverySalt"))
            return null;
        string nonce = q["hello"]?.ToString();
        lock (_helloGate)
        {
            PruneHello();
            if (nonce != null && _hello.TryGetValue(nonce, out var h) && h.State == "approved" && DateTime.UtcNow < h.ApprovedUntil)
            {
                _hello.Remove(nonce); // חד-פעמי
                return null;
            }
        }
        return Err("two_factor", "נדרש אימות Windows Hello (זיהוי פנים/טביעת אצבע/PIN)");
    }

    private async Task ServeOne(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(1000); // הבית הראשון חייב להגיע תוך שנייה (לקוח תוקע לא תופס מופע); אחריו — עד 5 שניות
            try
            {
                var buf = new byte[4096];
                var line = new MemoryStream();
                bool done = false;
                while (!done)
                {
                    int n = await pipe.ReadAsync(buf, 0, buf.Length, cts.Token);
                    if (n <= 0) break;
                    if (line.Length == 0) cts.CancelAfter(5000);
                    int nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                    line.Write(buf, 0, nl < 0 ? n : nl);
                    if (line.Length > PipeMaxLine) { HardLock.SvcLog("pipe: line too long"); return; }
                    done = nl >= 0;
                }
                if (!done && line.Length == 0) return;
                string text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                bool admin = false;
                string sidText = null;
                try
                {
                    pipe.RunAsClient(() =>
                    {
                        using var id = WindowsIdentity.GetCurrent();
                        admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                        sidText = id.User?.Value;
                    });
                }
                catch { admin = false; }
                _clientAdmin = admin;
                // דלי ניסיונות: SID + מורם/לא מורם (לא PID — תהליך חדש היה מאפס את החסימה); SID לא ידוע → דלי משותף
                // S1 ממצא 5: גשר שהשירות עצמו הפעיל (PID ברשימה + נתיב = ServiceExe) מקבל דלי נפרד, ולכן סקריפט גולמי באותו SID לא שורף את הדלי שלו
                bool ownBridge = false;
                try
                {
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint bp) && _ownSnapshot.Contains((int)bp))
                        ownBridge = BridgeProcs.SamePath(BridgeProcs.ImagePath((int)bp), HardLock.ServiceExe);
                }
                catch { }
                _clientKey = ClientBuckets.Key(sidText, admin, ownBridge);
                _clientOwnBridge = ownBridge;
                _clientPid = 0; _clientSession = 0;
                try
                {
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint cpid))
                    {
                        _clientPid = (int)cpid;
                        if (ProcessIdToSessionId(cpid, out uint csid)) _clientSession = (int)csid;
                    }
                }
                catch { }
                JsonObject res;
                try { res = Handle(JsonNode.Parse(text.Length == 0 ? "{}" : text) as JsonObject ?? new JsonObject()); }
                catch (Exception ex) { res = Err("internal", ex.Message); HardLock.SvcLog("pipe: " + ex); }
                byte[] outb = new UTF8Encoding(false).GetBytes(res.ToJsonString() + "\n");
                await pipe.WriteAsync(outb, 0, outb.Length, cts.Token);
                await pipe.FlushAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { HardLock.SvcLog("pipe conn: " + ex.Message); }
        }
    }

    private JsonObject Handle(JsonObject q)
    {
        lock (_gate)
        {
            string op = q["op"]?.ToString() ?? "";
            string pw = q["password"]?.ToString();
            JsonObject e;
            switch (op)
            {
                case "status":
                    return new JsonObject
                    {
                        // active = האם הנעילה נאכפת עכשיו (בפתיחה זמנית: false); lockOn = האם היא דלוקה בכלל
                        ["ok"] = true, ["version"] = Program.Version, ["configured"] = Configured, ["active"] = Enforced, ["lockOn"] = Active,
                        ["blockUsbStorage"] = _state["blockUsbStorage"]?.GetValue<bool>() == true,
                        ["scheduleEnabled"] = _state["scheduleEnabled"]?.GetValue<bool>() == true,
                        ["scheduleStart"] = _state["scheduleStart"]?.ToString() ?? "",
                        ["scheduleEnd"] = _state["scheduleEnd"]?.ToString() ?? "",
                        ["scheduleTz"] = _state["scheduleTz"]?.ToString() ?? "",
                        ["twoFactor"] = TwoFactorOn,
                        ["otzariaPath"] = _state["otzariaPath"]?.ToString() ?? "", ["sessions"] = _sessions,
                        ["policy"] = PolicyNode(),
                        ["tempUnlock"] = TempNode(),
                        ["stateCorrupt"] = _corrupt,
                        ["hasRecovery"] = !string.IsNullOrEmpty(_state["recoveryHash"]?.ToString()), // U1: הגשר מוחק את העותק המקומי רק כשהשירות מחזיק קוד שחזור
                        ["enforcementIssues"] = new JsonArray(_policyIssues.Select(kv => (JsonNode)(kv.Key + ": " + kv.Value)).ToArray()),
                    };

                case "recovery-token":
                {
                    // WHY (U1): challenge-response לשחרור חירום. הטוקן מונפק רק אחרי אימות קוד השחזור עצמו בפייפ (--unlock), נשמר בזיכרון בלבד
                    // ופג תוך 2 דקות; הקובץ בתיקיית המשתמש מכיל רק אותו. ה-hash השמור כבר לא משמש "הוכחה" (pass-the-hash). דלי ניסיונות נפרד מסיסמת ההורה
                    if (_corrupt) return Err("corrupt", "קובץ המצב פגום");
                    if (!Configured) return Err("bad_request", "רכיב ההקשחה עדיין לא הוגדר");
                    string pk = _clientKey + "|proof";
                    if (!_limiter.CanTry(pk, NowMs, out int rtWait)) return Err("too_many", $"יותר מדי ניסיונות שגויים. נסו שוב בעוד {rtWait} שניות");
                    string rcode = Secret.NormalizeRecovery(q["code"]?.ToString());
                    if (Matches(rcode, "recoveryHash", "recoverySalt"))
                    {
                        _limiter.Success(pk);
                        UpgradeHash(rcode, "recoveryHash", "recoverySalt");
                        return new JsonObject { ["ok"] = true, ["token"] = _recoveryTokens.Mint(NowMs) };
                    }
                    _limiter.Fail(pk, NowMs);
                    return Err("wrong_password", "קוד השחזור שגוי");
                }

                case "recovery-consume":
                {
                    // צריכת טוקן חירום חד-פעמי (מהגשר, אחרי שקרא אותו מקובץ הסימון). טוקן אקראי 192 ביט: ניחוש אינו ריאלי, ובכל זאת דלי נפרד
                    if (_corrupt) return Err("corrupt", "קובץ המצב פגום");
                    string ck = _clientKey + "|proof";
                    if (!_limiter.CanTry(ck, NowMs, out int rcWait)) return Err("too_many", $"יותר מדי ניסיונות שגויים. נסו שוב בעוד {rcWait} שניות");
                    if (_recoveryTokens.Consume(q["token"]?.ToString(), NowMs)) { _limiter.Success(ck); return Ok(); }
                    _limiter.Fail(ck, NowMs);
                    return Err("wrong_password", "טוקן שחרור החירום שגוי או שפג");
                }

                case "config-verify":
                {
                    // אימות MAC מועמד: בלי הגבלת חותם, כי התשובה היא כן/לא בלבד (אין אורקל חתימה). kind=seal: גם מונה מונוטוני (הגנת rollback)
                    if (!Configured) return Err("bad_request", "רכיב ההקשחה עדיין לא הוגדר");
                    byte[] vdata, vmac;
                    try { vdata = Convert.FromBase64String(q["data"]?.ToString() ?? ""); vmac = Convert.FromBase64String(q["mac"]?.ToString() ?? ""); }
                    catch { return new JsonObject { ["ok"] = true, ["valid"] = false }; }
                    if (vdata.Length > ConfigSealKey.MaxData) return Err("bad_request", "data ארוך מדי");
                    try { _sealKey ??= ConfigSealKey.LoadOrCreate(Path.Combine(HardLock.DataDir, "config-seal.key"), HardLock.SvcLog); }
                    catch (Exception ex) { HardLock.SvcLog("config-verify: " + ex.Message); return Err("internal", "מפתח החתימה אינו זמין"); }
                    byte[] expected;
                    if (q["kind"]?.ToString() == "seal")
                    {
                        long cur = _state["sealCounter"] is JsonValue scv && scv.TryGetValue<long>(out long scl) ? scl : 0;
                        long? presented = q["counter"] is JsonValue pcv && pcv.TryGetValue<long>(out long pcl) ? pcl : null;
                        if (!ConfigSealKey.CounterAccepts(cur, presented)) return new JsonObject { ["ok"] = true, ["valid"] = false };
                        expected = presented == null ? ConfigSealKey.Mac(_sealKey, vdata) : ConfigSealKey.MacWithCounter(_sealKey, vdata, presented.Value);
                    }
                    else expected = ConfigSealKey.Mac(_sealKey, vdata);
                    return new JsonObject { ["ok"] = true, ["valid"] = vmac.Length == expected.Length && CryptographicOperations.FixedTimeEquals(vmac, expected) };
                }

                case "config-sign":
                {
                    // WHY (U1): הצינור פתוח לכל משתמש. חתימה רק למנהל מורם, לגשר שהשירות עצמו הפעיל (DACL), או לתהליך מנתיב ה-ServiceExe שהציג סיסמת נעילה
                    // נכונה (נתיב לבדו לא מספיק: DOTNET_STARTUP_HOOKS ב-HKCU מריץ קוד זר ב-exe תקין, וחתימה שנעשתה כשהנעילה לא נאכפת נשארת תקפה)
                    if (!Configured) return Err("bad_request", "רכיב ההקשחה עדיין לא הוגדר");
                    if (_corrupt) return Err("corrupt", "קובץ המצב פגום");
                    bool pathOk = false;
                    try { pathOk = _clientPid > 0 && BridgeProcs.SamePath(BridgeProcs.ImagePath(_clientPid), HardLock.ServiceExe); } catch { }
                    bool pwOk = false;
                    if (!_clientAdmin && !_clientOwnBridge && pathOk && !string.IsNullOrEmpty(pw))
                    {
                        string sk = _clientKey + "|sign"; // דלי נפרד: ניחושים כאן לא שורפים את דלי הסיסמה הרגיל
                        if (!_limiter.CanTry(sk, NowMs, out int sgWait)) return Err("too_many", $"יותר מדי ניסיונות שגויים. נסו שוב בעוד {sgWait} שניות");
                        pwOk = !_corrupt && Matches(pw, "passwordHash", "salt");
                        if (pwOk) _limiter.Success(sk); else _limiter.Fail(sk, NowMs);
                    }
                    if (!ConfigSealKey.ClientMaySign(_clientAdmin, pathOk, _clientOwnBridge, pwOk))
                        return Err("forbidden", "חתימת הגדרות מותרת רק לגשר שהשירות הפעיל, למנהל מורם, או עם סיסמת הנעילה");
                    byte[] cdata;
                    try { cdata = Convert.FromBase64String(q["data"]?.ToString() ?? ""); }
                    catch { return Err("bad_request", "data אינו Base64 תקין"); }
                    if (cdata.Length > ConfigSealKey.MaxData) return Err("bad_request", "data ארוך מדי");
                    try { _sealKey ??= ConfigSealKey.LoadOrCreate(Path.Combine(HardLock.DataDir, "config-seal.key"), HardLock.SvcLog); }
                    catch (Exception ex) { HardLock.SvcLog("config-sign: " + ex.Message); return Err("internal", "מפתח החתימה אינו זמין"); }
                    if (q["kind"]?.ToString() == "seal")
                    {
                        // מונה מונוטוני: נשמר בדיסק לפני שהחתימה יוצאת, כך שחתימה שיצאה תמיד עם מונה שכבר נרשם (כשל שמירה = שגיאה, לא חתימה)
                        long next = (_state["sealCounter"] is JsonValue ov && ov.TryGetValue<long>(out long ol) ? ol : 0) + 1;
                        _state["sealCounter"] = next;
                        try { Save(); } catch (Exception ex) { HardLock.SvcLog("config-sign counter: " + ex.Message); return Err("internal", "שמירת מונה החתימה נכשלה"); }
                        return new JsonObject { ["ok"] = true, ["counter"] = next, ["mac"] = Convert.ToBase64String(ConfigSealKey.MacWithCounter(_sealKey, cdata, next)) };
                    }
                    return new JsonObject { ["ok"] = true, ["mac"] = Convert.ToBase64String(ConfigSealKey.Mac(_sealKey, cdata)) };
                }

                case "setup":
                    if (Configured && !_corrupt) return Err("bad_request", "כבר הוגדר");
                    // ביקורת #30 ממצא 2: רק מנהל (תהליך מורם) רשאי להגדיר לראשונה (וגם לשחזר מצב פגום: S1 ממצא 6)
                    if (!_clientAdmin) return Err("forbidden", "הגדרה ראשונה דורשת הרשאות מנהל");
                    foreach (var k in new[] { "passwordHash", "salt", "recoveryHash", "recoverySalt" })
                    {
                        string v = q[k]?.ToString();
                        if (string.IsNullOrEmpty(v)) return Err("bad_request", "חסר " + k);
                        _state[k] = v;
                    }
                    if (q["twoFactor"] is JsonValue tfSetup) _state["twoFactor"] = tfSetup.GetValue<bool>();
                    _corrupt = false; // הגדרה מחדש בידי מנהל מורם מחליפה את המצב הפגום
                    Save();
                    HardLock.SvcLog("service: הוגדרו סיסמה וקוד שחזור");
                    return Ok();

                case "verify":
                    if (q["recoveryOnly"]?.GetValue<bool>() == true)
                    {
                        // U1: "האם זה קוד השחזור" (פטור 2FA בביטול חירום). עותק ה-hash המקומי נמחק, ולכן השאלה עוברת לשירות. דלי נפרד ("|rec"): לא שורף את סיסמת ההורה
                        if (_corrupt || !Configured) return Err("bad_request", "רכיב ההקשחה לא מוגדר");
                        string rk = _clientKey + "|rec";
                        if (!_limiter.CanTry(rk, NowMs, out int rcw)) return Err("too_many", $"יותר מדי ניסיונות. נסו שוב בעוד {rcw} שניות");
                        string rnorm = Secret.NormalizeRecovery(pw);
                        if (Matches(rnorm, "recoveryHash", "recoverySalt")) { _limiter.Success(rk); UpgradeHash(rnorm, "recoveryHash", "recoverySalt"); return Ok(); }
                        _limiter.Fail(rk, NowMs);
                        return Err("wrong_password", "לא קוד השחזור");
                    }
                    return Check(pw, q["allowRecovery"]?.GetValue<bool>() == true) ?? Ok();

                case "enable":
                    if ((e = Check(pw, false)) != null) return e;
                    _state["active"] = true;
                    _state["activatedBy"] = "manual"; // נעילה ידנית לא תבוטל בסוף חלון התזמון
                    _state["tempUnlockUntil"] = "";   // הפעלה בסיסמה מסיימת פתיחה זמנית
                    string otz = q["otzariaPath"]?.ToString();
                    if ((e = ApplyEnableOtzariaPath(_state, otz)) != null) return e;
                    Save();
                    HardLock.SvcLog("service: נעילה הופעלה");
                    Task.Run(Tick);
                    return Ok();

                case "disable":
                    if ((e = Check(pw, true)) != null) return e;
                    if ((e = Need2fa(q, pw)) != null) return e;
                    _state["active"] = false;
                    _state["tempUnlockUntil"] = ""; // ביטול מלא: אין מה להחזיר בתום פתיחה זמנית
                    // ביטול בתוך חלון תזמון (גם בקוד שחזור) היה ננעל מחדש תוך שניות כי EvaluateSchedule מפעיל אותו
                    // שוב - מדלגים עד סוף החלון הנוכחי, כמו "ביטול הנעילה" באזהרת התזמון
                    if (_state["scheduleEnabled"]?.GetValue<bool>() == true &&
                        Sched.CurrentWindowEnd(_state["scheduleStart"]?.ToString(), _state["scheduleEnd"]?.ToString(), DateTimeOffset.UtcNow,
                            Sched.ResolveTz(_state["scheduleTz"]?.ToString())) is DateTimeOffset winEnd)
                        _state["skipUntil"] = winEnd.ToString("o");
                    Save();
                    HardLock.SvcLog("service: נעילה בוטלה");
                    Task.Run(Tick);
                    return Ok();

                case "set-password":
                    if ((e = Check(q["old"]?.ToString(), true)) != null) return e;
                    if ((e = Need2fa(q, q["old"]?.ToString())) != null) return e;
                    string np = q["new"]?.ToString() ?? "";
                    if (!PasswordPolicy.IsValid(np)) return Err("bad_request", $"הסיסמה חייבת להכיל לפחות {PasswordPolicy.MinLength} תווים"); // S1 ממצא 8: זהה לגשר
                    if (Matches(np, "tempUnlockHash", "tempUnlockSalt")) return Err("bad_request", "סיסמת הנעילה חייבת להיות שונה מסיסמת הפתיחה הזמנית");
                    byte[] salt = RandomNumberGenerator.GetBytes(16);
                    _state["salt"] = Convert.ToBase64String(salt);
                    _state["passwordHash"] = Secret.Hash(np, salt);
                    Save();
                    return Ok();

                case "settings":
                {
                    if ((e = Check(pw, false)) != null) return e;
                    bool? wantTf = q["twoFactor"] is JsonValue tfv ? tfv.GetValue<bool>() : null;
                    // הפעלה ראשונה דורשת Hello מוצלח בפועל (force), כדי לוודא שהמנגנון עובד לפני שנועלים את המנהל בעזרתו
                    if ((e = Need2fa(q, pw, force: wantTf == true && !TwoFactorOn)) != null) return e;
                    // קודם מאמתים את כל השדות, ורק אז כותבים — בקשה פגומה לא משאירה מצב חלקי
                    if ((e = ParsePolicy(q, out var policy)) != null) return e;
                    if ((e = ParseTempUnlock(q, wantTf, out var temp)) != null) return e;
                    if (wantTf != null) _state["twoFactor"] = wantTf.Value;
                    foreach (var kv in temp) _state[kv.Key] = kv.Value;
                    if (q["blockUsbStorage"] is JsonValue bu) _state["blockUsbStorage"] = bu.GetValue<bool>();
                    // שמירת אזור הזמן ברגע הגדרת התזמון (גם כשרק חלק מהשדות נשלח): מהנקודה הזו החישוב לפיו
                    if (q.ContainsKey("scheduleEnabled") || q.ContainsKey("scheduleStart") || q.ContainsKey("scheduleEnd"))
                        _state["scheduleTz"] = Sched.LocalTzId();
                    if (q["scheduleEnabled"] is JsonValue se) _state["scheduleEnabled"] = se.GetValue<bool>();
                    if (q["scheduleStart"]?.ToString() is string ss && Sched.IsValid(ss)) _state["scheduleStart"] = ss;
                    if (q["scheduleEnd"]?.ToString() is string ee && Sched.IsValid(ee)) _state["scheduleEnd"] = ee;
                    foreach (var kv in policy) _state[kv.Key] = kv.Value;
                    Save();
                    if (policy.Count > 0) HardLock.SvcLog("service: עודכנה מדיניות הנעילה (" + string.Join(", ", policy.Keys) + ")");
                    if (temp.Count > 0) HardLock.SvcLog("service: עודכנו הגדרות הפתיחה הזמנית (" + string.Join(", ", temp.Keys.Where(k => k != "tempUnlockSalt")) + ")");
                    Task.Run(Tick);
                    return Ok();
                }

                case "temp-unlock":
                {
                    // הסיסמה כאן היא סיסמת הפתיחה הזמנית בלבד — לא סיסמת הנעילה ולא קוד השחזור. בלי Windows Hello:
                    // זו בדיוק הסיסמה שנמסרת למי שאין לו את זהות המנהל (ולכן היא קיימת רק כשהאימות הדו-שלבי פעיל)
                    if (!Configured) return Err("bad_request", "רכיב ההקשחה עדיין לא הוגדר");
                    if (!_tempLimiter.CanTry(_clientKey, NowMs, out int tuWait))
                        return Err("too_many", $"יותר מדי ניסיונות שגויים. נסו שוב בעוד {tuWait} שניות");
                    if (!TwoFactorOn) return Err("forbidden", "פתיחה זמנית פועלת רק כשהאימות הדו-שלבי (Windows Hello) מופעל");
                    if (!TempUnlockUsable) return Err("bad_request", "הפתיחה הזמנית כבויה או שלא הוגדרה לה סיסמה");
                    if (!Matches(pw, "tempUnlockHash", "tempUnlockSalt"))
                    {
                        _tempLimiter.Fail(_clientKey, NowMs);
                        return Err("wrong_password", "סיסמת הפתיחה הזמנית שגויה");
                    }
                    _tempLimiter.Success(_clientKey);
                    if (!Active) return Err("bad_request", "המחשב אינו נעול כרגע");
                    if (TempSecondsLeft > 0) return Err("bad_request", "המחשב כבר פתוח זמנית");
                    int minutes = TempUnlockMinutes;
                    var until = TempUnlock.Until(DateTimeOffset.Now, minutes);
                    _tempEndTick = TempUnlock.EndTick(NowMs, minutes);
                    _state["tempUnlockUntil"] = until.ToString("o");
                    Save();
                    HardLock.SvcLog($"service: פתיחה זמנית ל-{minutes} דקות (עד {until:HH:mm:ss})");
                    Task.Run(Tick);
                    return new JsonObject { ["ok"] = true, ["until"] = until.ToString("o"), ["secondsLeft"] = TempSecondsLeft };
                }

                case "temp-unlock-end":
                    // סיום מוקדם רק מחזיר את האכיפה (הכיוון הבטוח), ולכן בלי סיסמה
                    if (string.IsNullOrEmpty(_state["tempUnlockUntil"]?.ToString())) return Ok();
                    _state["tempUnlockUntil"] = "";
                    Save();
                    HardLock.SvcLog("service: הפתיחה הזמנית הסתיימה ידנית");
                    Task.Run(Tick);
                    return Ok();

                case "skip":
                    if ((e = Check(pw, false)) != null) return e;
                    if ((e = Need2fa(q, pw)) != null) return e;
                    {
                        // ריק = ניקוי הדילוג; אחרת חייב להיות תאריך תקין, לכל היותר 36 שעות קדימה
                        string until = q["until"]?.ToString() ?? "";
                        if (until.Length > 0)
                        {
                            if (!DateTimeOffset.TryParse(until, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ud))
                                return Err("bad_request", "זמן הדילוג אינו תקין");
                            if (ud > DateTimeOffset.Now.AddHours(36)) ud = DateTimeOffset.Now.AddHours(36);
                            until = ud.ToString("o");
                        }
                        _state["skipUntil"] = until;
                    }
                    Save();
                    Task.Run(Tick);
                    return Ok();

                case "hello-start":
                {
                    if ((e = Check(pw, false)) != null) return e;
                    if (_clientSession <= 0) return Err("bad_request", "לא זוהה סשן משתמש לחלון האימות");
                    lock (_helloGate) { PruneHello(); if (_hello.Count >= 4) return Err("too_many", "יותר מדי בקשות אימות פתוחות"); }
                    string hn = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                    if (!Native.LaunchInSession(_clientSession, "\"" + HardLock.ServiceExe + "\" --hello-helper " + hn, out int hpid, protect: false))
                        return Err("internal", "לא ניתן להפעיל את חלון האימות בסשן של המשתמש");
                    lock (_helloGate) _hello[hn] = new HelloReq { Pid = hpid, Expires = DateTime.UtcNow.AddSeconds(80) };
                    return new JsonObject { ["ok"] = true, ["nonce"] = hn };
                }

                case "hello-status":
                {
                    string sn = q["nonce"]?.ToString() ?? "";
                    lock (_helloGate)
                    {
                        PruneHello();
                        return new JsonObject { ["ok"] = true, ["state"] = _hello.TryGetValue(sn, out var sh) ? sh.State : "expired" };
                    }
                }

                case "hello-done":
                {
                    string dn = q["nonce"]?.ToString() ?? "";
                    lock (_helloGate)
                    {
                        if (!_hello.TryGetValue(dn, out var dh) || dh.State != "pending" || DateTime.UtcNow > dh.Expires)
                            return Err("bad_request", "בקשת אימות לא קיימת או שפג תוקפה");
                        bool fromHelper = false;
                        try
                        {
                            using var hp = Process.GetProcessById(_clientPid);
                            fromHelper = _clientPid == dh.Pid &&
                                string.Equals(Path.GetFullPath(hp.MainModule?.FileName ?? ""), Path.GetFullPath(HardLock.ServiceExe), StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }
                        if (!fromHelper) return Err("forbidden", "הדיווח אינו מתהליך העזר שהשירות הפעיל");
                        bool okv = q["ok"]?.GetValue<bool>() == true;
                        dh.State = okv ? "approved" : "failed";
                        dh.ApprovedUntil = DateTime.UtcNow.AddMinutes(2);
                        return Ok();
                    }
                }

                case "stop-bridges":
                    // יציאה מורשית של הגשרים (עדכון/התקנה) בלי taskkill: רק כשהנעילה לא נאכפת (אחרת גשר חייב לרוץ) — והשירות עצמו הורג
                    if (Configured && Enforced) return Err("forbidden", "הנעילה נאכפת — לא ניתן לסגור את הגשר");
                    Task.Run(() => HardLock.StopBridgesAt(HardLock.ServiceExe, _clientPid));
                    return Ok();

                case "uninstall":
                    // S1 ממצא 6: מצב לא מוגדר/פגום כבר לא פותח בלי אימות: נדרש מנהל מורם (מסלול חירום: cmd מורם עם --uninstall, או sc delete)
                    if (_corrupt || !Configured)
                    {
                        if (!_clientAdmin) return Err("forbidden", "הסרה כשהרכיב לא מוגדר/פגום דורשת הרשאות מנהל מורם");
                    }
                    else
                    {
                        if ((e = Check(pw, true)) != null) return e;
                        if ((e = Need2fa(q, pw)) != null) return e;
                    }
                    _uninstalling = true; // עוצר את אכיפת האתחול (BootPolicy) לפני ההסרה
                    _state["active"] = false;
                    _state["uninstalling"] = true;
                    Save();
                    Task.Run(SelfUninstall);
                    return Ok();

                default:
                    return Err("unknown_op", "פקודה לא מוכרת: " + op); // קוד ייעודי: הגשר מבחין בין שירות ישן לשגיאה אחרת (ר' ConfigSeal.ClassifyError)
            }
        }
    }

    // ---------- שומר ----------

    private int _tickBusy;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1) return;
        try
        {
            bool active, blockUsb, hardenBoot;
            lock (_gate)
            {
                CheckClockJump();     // שעון שהוזז מעבר ל-2 דקות מבטל פתיחה זמנית (fail-closed)
                EvaluateSchedule();   // תזמון לפי שעות — עשוי להפעיל/לבטל את הנעילה
                ExpireTempUnlock();   // פתיחה זמנית שהסתיימה — האכיפה חוזרת כבר בטיק הזה
                active = Enforced;
                hardenBoot = BootPolicy.ShouldHarden(Configured, _uninstalling);
                blockUsb = _state["blockUsbStorage"]?.GetValue<bool>() == true;
            }
            EnforcePolicies(active);
            EnforceUsb(active && blockUsb);
            EnforceOffice(active);
            // S1 ממצא 3: Safe Mode/WinRE/bcd נשארים מוקשחים כל עוד השירות Configured (לא רק כשהנעילה נאכפת); שחרור בהסרה מלאה בלבד
            BootHardening.Enforce(hardenBoot);
            // הגשר נבדק ב-WatchBridge (כל שנייה) ולא כאן (כל 5 שניות): חלון ההרמה מחדש קוצר
        }
        catch (Exception ex) { HardLock.SvcLog("tick: " + ex.Message); }
        finally { Interlocked.Exchange(ref _tickBusy, 0); }
    }

    /// <summary>
    /// תזמון נעילה לפי שעות — מקור האמת כשהשירות מותקן. נקרא תחת _gate מתוך Tick.
    /// בתוך החלון מפעיל נעילה (activatedBy=schedule); בסוף החלון מבטל רק נעילה שהתזמון הפעיל
    /// (נעילה ידנית נשמרת). נעילה ידנית שבוטלה בתוך החלון תחזור בטיק הבא — זו מהות התזמון.
    /// </summary>
    private void EvaluateSchedule()
    {
        string start = _state["scheduleStart"]?.ToString();
        string end = _state["scheduleEnd"]?.ToString();
        // דילוג ידני (מ"ביטול הנעילה" באזהרה) או תזמון כבוי → כמו "מחוץ לחלון"
        // מושווה כרגע מוחלט (DateTimeOffset) — חסין למעבר שעון קיץ/חורף ולסוג ה-Kind
        bool skip = DateTimeOffset.TryParse(_state["skipUntil"]?.ToString(), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var su) && DateTimeOffset.Now < su;
        // החלון מחושב לפי אזור הזמן ששמרנו בהגדרת התזמון, לא לפי אזור הזמן הנוכחי של Windows (שמשתמש רגיל רשאי לשנות)
        var tz = Sched.ResolveTz(_state["scheduleTz"]?.ToString());
        bool inWindow = _state["scheduleEnabled"]?.GetValue<bool>() == true && !skip && Sched.InWindow(start, end, DateTimeOffset.UtcNow, tz);
        bool active = Active;
        string by = _state["activatedBy"]?.ToString() ?? "manual";
        if (inWindow && !active)
        {
            _state["active"] = true;
            _state["activatedBy"] = "schedule";
            Save();
            HardLock.SvcLog($"service: תזמון — בתוך חלון הנעילה ({start}–{end}), נעילה הופעלה");
        }
        else if (!inWindow && active && by == "schedule")
        {
            _state["active"] = false;
            Save();
            HardLock.SvcLog("service: תזמון — מחוץ לחלון, נעילה בוטלה");
        }
    }

    /// <summary>
    /// נקרא תחת _gate מתוך Tick (כל 5 שניות). אם שעון הקיר התקדם שונה מהמונוטוני ביותר מ-2 דקות — מישהו הזיז את השעון
    /// (או שהמחשב ישן; גם זה מסיים את הפתיחה, הכיוון הבטוח) — הפתיחה הזמנית מבוטלת והנעילה חוזרת.
    /// </summary>
    private void CheckClockJump()
    {
        bool jumped = _clock.Sample(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), NowMs);
        if (!jumped) return;
        // skipUntil (דילוג תזמון) מושווה לשעון קיר: הזזת שעון אחורה הייתה מאריכה אותו עד 36 שעות — גם הוא מתנקה (fail-closed)
        bool hadTemp = !string.IsNullOrEmpty(_state["tempUnlockUntil"]?.ToString());
        bool hadSkip = !string.IsNullOrEmpty(_state["skipUntil"]?.ToString());
        if (!hadTemp && !hadSkip) return;
        if (hadTemp) { _state["tempUnlockUntil"] = ""; _tempEndTick = 0; }
        if (hadSkip) _state["skipUntil"] = "";
        Save();
        HardLock.SvcLog("service: זוהתה קפיצת שעון (מעל 2 דקות) — " + (hadTemp ? "הפתיחה הזמנית " : "") + (hadSkip ? "ודילוג התזמון " : "") + "בוטלו והנעילה חוזרת");
    }

    /// <summary>
    /// מנקה זמן סיום של פתיחה זמנית שעבר (או שהפך ללא שמיש). נקרא תחת _gate מתוך Tick. ההחזרה של האכיפה עצמה
    /// אינה תלויה בניקוי (Enforced מחושב לפי הזמן), זה רק כדי שהמצב השמור והיומן יהיו ברורים.
    /// </summary>
    private void ExpireTempUnlock()
    {
        if (string.IsNullOrEmpty(_state["tempUnlockUntil"]?.ToString()) || TempSecondsLeft > 0) return;
        _state["tempUnlockUntil"] = "";
        Save();
        HardLock.SvcLog(Active ? "service: הפתיחה הזמנית הסתיימה — הנעילה נאכפת שוב" : "service: הפתיחה הזמנית הסתיימה (הנעילה כבר אינה דלוקה)");
    }

    /// <summary>מדיניות בכל חשבון מחובר (HKEY_USERS\SID): מנהל המשימות ועורך הרישום חסומים; מוחזרת אם נמחקה</summary>
    private void EnforcePolicies(bool active)
    {
        HashSet<string> touched;
        lock (_gate)
            touched = (_state["touched"] as JsonArray)?.Select(x => x?.ToString()).Where(x => x != null).ToHashSet()
                      ?? new HashSet<string>();
        bool changed = false;
        foreach (string sid in Registry.Users.GetSubKeyNames().Where(n => UserSid.IsMatch(n)))
        {
            try
            {
                if (active)
                {
                    if (!ApplyPolicyValues(sid)) { RepairPolicyKeyAcl(sid); ApplyPolicyValues(sid); }
                    if (touched.Add(sid)) changed = true;
                }
                else if (touched.Contains(sid))
                {
                    using var k = Registry.Users.OpenSubKey(sid + "\\" + PoliciesSub, true);
                    k?.DeleteValue("DisableTaskMgr", false);
                    k?.DeleteValue("DisableRegistryTools", false);
                    touched.Remove(sid);
                    changed = true;
                }
            }
            catch (Exception ex) { _policyIssues[sid] = ex.Message; HardLock.SvcLog("policy " + sid + ": " + ex.Message); }
        }
        if (changed)
        {
            lock (_gate)
            {
                _state["touched"] = new JsonArray(touched.Select(s => (JsonNode)s).ToArray());
                Save();
            }
        }
    }

    /// <summary>כותב ומאמת (קריאה חוזרת) את ערכי החסימה. כשל = ממצא גלוי ב-status (enforcementIssues), לא נבלע. true = אומת</summary>
    private bool ApplyPolicyValues(string sid)
    {
        try
        {
            using var k = Registry.Users.CreateSubKey(sid + "\\" + PoliciesSub);
            foreach (var name in new[] { "DisableTaskMgr", "DisableRegistryTools" })
            {
                if (!(k.GetValue(name) is int v && v == 1)) k.SetValue(name, 1, RegistryValueKind.DWord);
                if (!(k.GetValue(name) is int v2 && v2 == 1)) { _policyIssues[sid] = name + " לא אומת"; return false; }
            }
            _policyIssues.TryRemove(sid, out _);
            return true;
        }
        catch (Exception ex) { _policyIssues[sid] = ex.Message; return false; }
    }

    /// <summary>
    /// ניסיון תיקון (best effort): המשתמש בעלים של המפתח ויכול לשים Deny ל-SYSTEM. מסירים Deny ומחזירים Allow ל-SYSTEM. אם הבעלים יחזור על כך,
    /// GuardTools (הריגה בשירות) הוא הגיבוי. לא נבדק בחי.
    /// </summary>
    private static void RepairPolicyKeyAcl(string sid)
    {
        try
        {
            using var k = Registry.Users.OpenSubKey(sid + "\\" + PoliciesSub, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
            if (k == null) return;
            var acl = k.GetAccessControl();
            foreach (RegistryAccessRule r in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                if (r.AccessControlType == AccessControlType.Deny) acl.RemoveAccessRuleSpecific(r);
            acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            k.SetAccessControl(acl);
            HardLock.SvcLog("policy " + sid + ": תוקן ACL של Policies\\System");
        }
        catch (Exception ex) { HardLock.SvcLog("policy repair " + sid + ": " + ex.Message); }
    }

    /// <summary>מדיניות Office (VBAWarnings/AccessVBOM) כשהנעילה נאכפת; שחרור מחזיר את המקור. ר' OfficePolicy</summary>
    private void EnforceOffice(bool active)
    {
        try
        {
            var issues = new List<string>();
            lock (_gate)
            {
                var orig = _state["officeOriginal"] as JsonObject ?? new JsonObject();
                // persist: המקורות נשמרים לדיסק לפני הכתיבה הראשונה, כך שקריסה באמצע לא תשאיר ערך שלנו שייחשב מקור (ר' OfficePolicy.Apply)
                if (OfficePolicy.Apply(new OfficePolicy.RegistryStore(), active, orig, issues, () => { _state["officeOriginal"] = orig; Save(); }))
                {
                    if (orig.Count > 0) _state["officeOriginal"] = orig; else _state.Remove("officeOriginal");
                    Save();
                    HardLock.SvcLog("service: מדיניות Office " + (active ? "הופעלה" : "שוחררה"));
                }
            }
            foreach (string i in issues) _policyIssues["office:" + i] = i;
            if (issues.Count == 0) foreach (var k in _policyIssues.Keys.Where(k => k.StartsWith("office:")).ToList()) _policyIssues.TryRemove(k, out _);
        }
        catch (Exception ex) { HardLock.SvcLog("office: " + ex.Message); }
    }

    private void EnforceUsb(bool block)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(UsbStorKey, true);
            if (k == null) return;
            bool hasOriginal;
            int orig;
            lock (_gate)
            {
                hasOriginal = _state["usbOriginal"] != null;
                orig = hasOriginal ? _state["usbOriginal"].GetValue<int>() : 3;
            }
            int cur = k.GetValue("Start") is int v ? v : 3;
            if (block && cur != 4)
            {
                lock (_gate) { _state["usbOriginal"] = cur; Save(); }
                k.SetValue("Start", 4, RegistryValueKind.DWord);
                HardLock.SvcLog("service: אחסון USB נחסם");
            }
            else if (!block && hasOriginal)
            {
                k.SetValue("Start", orig == 4 ? 3 : orig, RegistryValueKind.DWord);
                lock (_gate) { _state.Remove("usbOriginal"); Save(); }
                HardLock.SvcLog("service: אחסון USB שוחרר");
            }
        }
        catch (Exception ex) { HardLock.SvcLog("usb: " + ex.Message); }
    }

    /// <summary>טיימר של שנייה: כשהנעילה נאכפת — מוודא שהגשר רץ בכל סשן, ומקשיח את התהליכים שלו</summary>
    private void WatchBridge()
    {
        if (!Monitor.TryEnter(_bridgeGate)) return;
        try
        {
            bool enforced;
            lock (_gate) enforced = Configured && Enforced;
            if (enforced) { EnsureBridgeInSessions(); GuardTools(); }
        }
        catch (Exception ex) { HardLock.SvcLog("bridge watch: " + ex.Message); }
        finally { Monitor.Exit(_bridgeGate); }
    }

    /// <summary>
    /// בכל חשבון מחובר (כולל חשבון שני) הגשר חייב לרוץ; אם נסגר — מופעל מחדש תוך 3 שניות (היה 15 שניות חסד ו-20 בין ניסיונות).
    /// "רץ" = תהליך שנתיב ה-exe שלו הוא ה-exe של השירות (Program Files), לא רק שם תהליך זהה. תהליך בשם הגשר מנתיב אחר
    /// הוא מתחזה/עותק משתמש שמחזיק את ה-mutex של הסשן — נהרג, והגשר האמיתי עולה במקומו.
    /// </summary>
    private void EnsureBridgeInSessions()
    {
        if (!File.Exists(HardLock.ServiceExe)) return;
        var active = Native.ActiveSessions();
        _sessions = active.Count;
        var procs = BridgeProcs.Scan();
        var running = BridgeProcs.SessionsRunning(procs, HardLock.ServiceExe);

        // הגשר האמיתי שכבר רץ: מקשיחים (גם אם עלה מ-Run של המשתמש, בלי ה-DACL שהשירות נותן) — תהליך וחוטים
        // גשר אמיתי שהשירות לא יצר (Run/explorer/משתמש): למשתמש handle מלא אליו, ו-HardenExisting לא מבטל handle קיים → נהרג,
        // והשירות מעלה גשר משלו (נולד עם DACL). גשרים שהשירות יצר נבדקים גם מול זמן ההתחלה (PID שהוחזר לא נחשב)
        PruneOwn(procs);
        var untrusted = BridgeProcs.Untrusted(procs, HardLock.ServiceExe, _ownBridges.Keys.ToHashSet());
        foreach (var u in untrusted)
        {
            try { using var pr = Process.GetProcessById(u.Pid); pr.Kill(); HardLock.SvcLog($"service: נהרג גשר שלא הופעל ע\"י השירות (pid {u.Pid}, session {u.Session}) — יעלה גשר מוגן"); }
            catch (Exception ex) { HardLock.SvcLog("service: הריגת גשר לא-מהימן נכשלה: " + ex.Message); }
        }
        if (untrusted.Count > 0) { procs = procs.Where(p => !untrusted.Any(u => u.Pid == p.Pid)).ToList(); running = BridgeProcs.SessionsRunning(procs, HardLock.ServiceExe); }
        var real = procs.Where(p => BridgeProcs.SamePath(p.Path, HardLock.ServiceExe)).ToList();
        // חוטים: טיימר ייעודי (ThreadGuardTick) כל ~200ms במקום פעם בשנייה (S1 ממצא 1)
        _guardPids = real.Select(p => p.Pid).Concat(_ownBridges.Keys).ToHashSet();
        _ownSnapshot = _ownBridges.Keys.ToHashSet();

        var now = DateTime.UtcNow;
        foreach (int sid in active)
        {
            if (!_sessionSeen.ContainsKey(sid)) _sessionSeen[sid] = now;
            if (running.Contains(sid)) { _launchStreak.Remove(sid); _immediateSessions.TryRemove(sid, out _); continue; }

            // תהליכים בשם הגשר מנתיב אחר בסשן הזה — מחזיקים את ה-mutex ומונעים מהאמיתי לעלות
            foreach (var imp in BridgeProcs.Impostors(procs, HardLock.ServiceExe).Where(p => p.Session == sid))
            {
                try { using var pr = Process.GetProcessById(imp.Pid); pr.Kill(); HardLock.SvcLog($"service: נהרג תהליך בשם הגשר מנתיב אחר ({imp.Path}, session {sid})"); }
                catch (Exception ex) { HardLock.SvcLog("service: הריגת תהליך בשם הגשר מנתיב אחר נכשלה: " + ex.Message); }
            }

            _launchStreak.TryGetValue(sid, out int streak);
            DateTime? last = _lastLaunch.TryGetValue(sid, out var l) ? l : null;
            bool immediate = _immediateSessions.ContainsKey(sid);
            if (!RelaunchPolicy.ShouldLaunch(now, _sessionSeen[sid], last, streak, immediate)) continue;
            _immediateSessions.TryRemove(sid, out _);
            _lastLaunch[sid] = now;
            _launchStreak[sid] = streak + 1;
            bool ok = Native.LaunchInSession(sid, "\"" + HardLock.ServiceExe + "\" --background --portable --from-service", out int newPid);
            if (ok) { TrackOwn(newPid); _guardPids = _guardPids.Append(newPid).ToHashSet(); _ownSnapshot = _ownBridges.Keys.ToHashSet(); }
            HardLock.SvcLog("service: הגשר לא רץ בחשבון (session " + sid + ") — " + (ok ? "הופעל מחדש" : "ההפעלה נכשלה"));
        }
        foreach (int gone in _sessionSeen.Keys.Except(active).ToList()) { _sessionSeen.Remove(gone); _launchStreak.Remove(gone); _lastLaunch.Remove(gone); }
    }

    /// <summary>
    /// S1 ממצא 1: מקשיח את חוטי הגשרים האמיתיים כל ~200ms (היה שנייה). נתיב התמונה נבדק בכל סבב, כדי שמזהה שהוחזר לתהליך אחר לא יקבל DACL.
    /// נשאר חלון קצר: סגירה מלאה = TokenDefaultDacl (ProcessDacl.UseTokenDefaultDacl), כבוי מאחורי דגל עד בדיקה בסשן Windows חי
    /// (משפיע על תהליכי-בן ואובייקטי סנכרון של הגשר).
    /// </summary>
    private void ThreadGuardTick()
    {
        var pids = _guardPids;
        if (pids == null || pids.Count == 0) return;
        if (!Monitor.TryEnter(_threadGate)) return;
        try
        {
            var ok = pids.Where(p => BridgeProcs.SamePath(BridgeProcs.ImagePath(p), HardLock.ServiceExe)).ToHashSet();
            ProcessDacl.HardenThreads(ok);
        }
        catch (Exception ex) { HardLock.SvcLog("thread guard: " + ex.Message); }
        finally { Monitor.Exit(_threadGate); }
    }

    /// <summary>
    /// S1 ממצא 2: כשהנעילה נאכפת מורג taskmgr/regedit/mmc של משתמש רגיל (נתיב System32; מורם נחסך). לא תלוי במפתח Policies\System
    /// שהמשתמש בעלים שלו ויכול לחסום ממנו את SYSTEM.
    /// </summary>
    private void GuardTools()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (string name in ToolGuard.Names)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in ps)
                using (p)
                {
                    try
                    {
                        var (_, elevated) = ClientIdentity(p.Id);
                        if (!ToolGuard.ShouldKill(name, BridgeProcs.ImagePath(p.Id), elevated, win)) continue;
                        p.Kill();
                        HardLock.SvcLog($"service: נהרג {name} (pid {p.Id}) בזמן נעילה");
                    }
                    catch (Exception ex) { HardLock.SvcLog("tool guard " + name + ": " + ex.Message); }
                }
        }
        GuardRenamedTools(win);
    }

    private readonly Dictionary<string, string> _origNameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WHY (U1): העתק של taskmgr/regedit/mmc בשם אחר (בנתיב כתיב) לא נתפס לפי שם. כאן עוברים על תהליכים מחוץ לתיקיית Windows וקוראים את
    /// OriginalFilename מה-VERSIONINFO (מטמון לפי נתיב+גודל+זמן שינוי, כדי שהסריקה תישאר זולה). לא נבדק בחי.
    /// </summary>
    private void GuardRenamedTools(string win)
    {
        Process[] all;
        try { all = Process.GetProcesses(); } catch { return; }
        string winPrefix = win.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var p in all)
            using (p)
            {
                try
                {
                    if (p.Id <= 4 || p.Id == Environment.ProcessId) continue;
                    string img = BridgeProcs.ImagePath(p.Id);
                    if (string.IsNullOrEmpty(img) || img.StartsWith(winPrefix, StringComparison.OrdinalIgnoreCase)) continue; // תחת Windows: כתיב למנהלים בלבד, והשם נבדק למעלה
                    string orig = OriginalNameOf(img);
                    if (string.IsNullOrEmpty(orig)) continue;
                    var (_, elevated) = ClientIdentity(p.Id);
                    if (!ToolGuard.ShouldKill(p.ProcessName, img, elevated, win, orig)) continue;
                    p.Kill();
                    HardLock.SvcLog($"service: נהרג העתק של {orig} ({img}, pid {p.Id}) בזמן נעילה");
                }
                catch { }
            }
        if (_origNameCache.Count > 2000) _origNameCache.Clear();
    }

    private string OriginalNameOf(string img)
    {
        try
        {
            var fi = new FileInfo(img);
            string key = img + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            if (_origNameCache.TryGetValue(key, out var v)) return v;
            string o = FileVersionInfo.GetVersionInfo(img).OriginalFilename ?? "";
            _origNameCache[key] = o;
            return o;
        }
        catch { return null; }
    }

    private void TrackOwn(int pid)
    {
        try { using var pr = Process.GetProcessById(pid); _ownBridges[pid] = pr.StartTime; } catch { _ownBridges[pid] = DateTime.MinValue; }
    }

    /// <summary>מסיר מהרשימה תהליכים שנגמרו או ש-PID שלהם נלקח (זמן התחלה שונה)</summary>
    private void PruneOwn(List<ProcInfo> procs)
    {
        foreach (int pid in _ownBridges.Keys.ToList())
        {
            if (!procs.Any(p => p.Pid == pid)) { _ownBridges.Remove(pid); continue; }
            try { using var pr = Process.GetProcessById(pid); if (_ownBridges[pid] != DateTime.MinValue && pr.StartTime != _ownBridges[pid]) _ownBridges.Remove(pid); }
            catch { _ownBridges.Remove(pid); }
        }
    }

    private void SelfUninstall()
    {
        try
        {
            Thread.Sleep(500);
            EnforcePolicies(false);
            EnforceUsb(false);
            EnforceOffice(false);
            BootHardening.Enforce(false); // BOOTHARDENING: אחרת SafeBoot/WinRE נשארים נעולים אחרי הסרה
            HardLock.SvcLog("service: הסרה");
            HardLock.UnregisterMachineRun();
            Config.Run("sc.exe", "delete " + HardLock.ServiceName, 10000);
            // הגשר רץ מ-Program Files ומוגן מהריגה ע"י המשתמש (DACL) — בלי זה rmdir להלן נכשל על exe נעול
            Thread.Sleep(2500); // לגשר/לתהליך ההסרה זמן לסיים את הזרימה שלהם לפני ההריגה
            HardLock.StopBridgesAt(HardLock.ServiceExe);
            // מחיקת הקבצים אחרי שהשירות ייעצר
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping -n 6 127.0.0.1 >nul & rmdir /s /q \"{HardLock.ProgDir}\" & rmdir /s /q \"{HardLock.DataDir}\"")
                { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) { HardLock.SvcLog("uninstall: " + ex.Message); }
        Stop();
    }
}

/// <summary>הפעלת תהליך בחשבון של משתמש מחובר מתוך שירות SYSTEM</summary>
internal static class Native
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO { public int SessionId; public IntPtr WinStationName; public int State; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public static List<int> ActiveSessions()
    {
        var list = new List<int>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr p, out int n)) return list;
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < n; i++)
            {
                var s = Marshal.PtrToStructure<WTS_SESSION_INFO>(p + i * size);
                if (s.State == 0 /* WTSActive */ && s.SessionId != 0) list.Add(s.SessionId);
            }
        }
        finally { WTSFreeMemory(p); }
        return list;
    }

    public static bool LaunchInSession(int sessionId, string commandLine) => LaunchInSession(sessionId, commandLine, out _);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    /// <summary>
    /// protect=true (ברירת מחדל): התהליך והחוט הראשי נוצרים עם DACL מצומצם (ProcessDacl) — המשתמש לא יכול להרוג/להזריק/להקפיא.
    /// protect=false רק לתהליך העזר של Windows Hello: רכיבי Hello (CredentialUIBroker) עשויים לדרוש גישה לתהליך הקורא,
    /// ולא ניתן לבדוק זאת אוטומטית — כשל שם היה חוסם את כל פעולות ה-2FA. (העזר חי עד 80 שניות והשירות מקבל ממנו תשובה רק מה-PID שלו)
    /// </summary>
    public static bool LaunchInSession(int sessionId, string commandLine, out int pid, bool protect = true)
    {
        pid = 0;
        if (!WTSQueryUserToken(sessionId, out IntPtr token)) return false;
        try { return Launch(token, commandLine, HardLock.ProgDir, protect, out pid, out _); }
        finally { CloseHandle(token); }
    }

    /// <summary>
    /// הפעלה בפועל. token==Zero → CreateProcess רגיל (לבדיקות). עם protect מועבר ל-CreateProcess SECURITY_ATTRIBUTES של תהליך וחוט.
    /// הסביבה: הבלוק של המשתמש מסונן מ-DOTNET_*/COMPlus_*/COR_*/CORECLR_* (ר' EnvBlock). hProcess מוחזר לקורא (לבדיקות) או נסגר
    /// </summary>
    internal static bool Launch(IntPtr token, string commandLine, string dir, bool protect, out int pid, out IntPtr hProcessOut, bool closeHandles = true, string procSddl = null)
    {
        pid = 0; hProcessOut = IntPtr.Zero;
        IntPtr env = IntPtr.Zero, rawEnv = IntPtr.Zero, paProc = IntPtr.Zero, paThr = IntPtr.Zero, sdProc = IntPtr.Zero, sdThr = IntPtr.Zero;
        try
        {
            if (token != IntPtr.Zero && CreateEnvironmentBlock(out rawEnv, token, false))
                env = EnvBlock.Build(EnvBlock.WithBundleDir(EnvBlock.Filter(EnvBlock.Parse(rawEnv)), HardLock.BundleDir, HardLock.BundleReady));
            if (token != IntPtr.Zero && protect && ProcessDacl.UseTokenDefaultDacl)
                ProcessDacl.SetTokenDefault(token, ProcessDacl.TokenDefaultSddl()); // חוטים חדשים נולדים מוקשחים (כבוי כברירת מחדל — ר' הערה)
            if (protect)
            {
                paProc = ProcessDacl.AllocAttributes(procSddl ?? ProcessDacl.Sddl(ProcessDacl.UserProcessMask, true), out sdProc);
                paThr = ProcessDacl.AllocAttributes(ProcessDacl.Sddl(ProcessDacl.UserThreadMask, true), out sdThr);
                // לא מצליחים לבנות DACL → לא מפעילים בלי הגנה בשקט; מנסים בלי (עדיף גשר רץ מגשר שלא עולה) ורושמים ביומן
                if (paProc == IntPtr.Zero || paThr == IntPtr.Zero)
                {
                    HardLock.SvcLog("launch: בניית DACL נכשלה — הגשר יופעל בלי הגנת תהליך");
                    ProcessDacl.FreeAttributes(paProc, sdProc); ProcessDacl.FreeAttributes(paThr, sdThr);
                    paProc = paThr = sdProc = sdThr = IntPtr.Zero;
                }
            }
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = token != IntPtr.Zero ? @"winsta0\default" : null };
            const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
            uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
            PROCESS_INFORMATION pi;
            bool ok = token != IntPtr.Zero
                ? CreateProcessAsUser(token, null, new StringBuilder(commandLine), paProc, paThr, false, flags, env, dir, ref si, out pi)
                : CreateProcessW(null, new StringBuilder(commandLine), paProc, paThr, false, flags, env, dir, ref si, out pi);
            if (ok)
            {
                pid = pi.dwProcessId;
                CloseHandle(pi.hThread);
                if (closeHandles) CloseHandle(pi.hProcess); else hProcessOut = pi.hProcess;
            }
            return ok;
        }
        finally
        {
            if (rawEnv != IntPtr.Zero) DestroyEnvironmentBlock(rawEnv);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
            ProcessDacl.FreeAttributes(paProc, sdProc);
            ProcessDacl.FreeAttributes(paThr, sdThr);
        }
    }
}

/// <summary>אילו אירועי SERVICE_CONTROL_SESSIONCHANGE מפעילים הרמת גשר מיידית (כניסה, ביטול נעילת מסך, חיבור קונסול/מרוחק)</summary>
internal static class SessionTrigger
{
    internal static bool IsLogonLike(System.ServiceProcess.SessionChangeReason r) =>
        r is System.ServiceProcess.SessionChangeReason.SessionLogon or System.ServiceProcess.SessionChangeReason.SessionUnlock
          or System.ServiceProcess.SessionChangeReason.ConsoleConnect or System.ServiceProcess.SessionChangeReason.RemoteConnect;
}
