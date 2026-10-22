using System.Management;
using System.Text.Json;
using System.Linq;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>
/// הקשחת BIOS/UEFI מתוך תוכנה: הגדרת סיסמת BIOS, וחיפוש והפעלה של הגדרה שחוסמת אתחול מהתקן USB/חיצוני.
/// אין ב-Windows ממשק גנרי לכתיבה ל-BIOS — רק ממשקי WMI ספציפיים ליצרן, שכל יצרן בוחר אם לחשוף ואיך:
///
///   - Lenovo: root\wmi, Lenovo_BiosSetting/Lenovo_SetBiosSetting/Lenovo_SaveBiosSettings/Lenovo_SetBiosPassword.
///     מתועד ועובד ברוב דגמי ThinkPad/ThinkCentre, כולל בלי תוכנה נוספת מותקנת.
///   - HP: root\hp\instrumentedBIOS, HP_BIOSEnumeration (קריאה) ו-HP_BIOSSettingInterface.SetBIOSSetting (כתיבה).
///     עובד נייטיב בדגמים עסקיים (EliteBook/EliteDesk/ProBook/Z); לא בטוח בדגמי צרכן.
///   - Dell: אין API מתועד בלי Dell Command | Monitor מותקן בנפרד — לא ממומש, רק זיהוי+הסבר.
///   - Asus: אין API צרכני כלל (רק ACT, עסקי בלבד, Intel Gen12+ 2022 ואילך) — לא ממומש, רק זיהוי+הסבר.
///
/// כל פעולה מדווחת בכנות: attempted/success/message. לעולם לא "הצלחה" בלי אישור בפועל מה-WMI.
/// ר' docs/SECURITY_NOTES.md לפירוט מלא של המגבלות (כולל סיכון סיסמה שנשכחה).
/// </summary>
public static class BiosHardening
{
    public static void Register()
    {
        Server.Route("/bios/status", _ => StatusWithBoot());
        Server.Route("/bios/harden", Harden);
    }

    // ---------- זיהוי יצרן (Win32_ComputerSystem, כמו שאר הגשר משתמש ב-WMI — ר' Programs.cs/SysApi.cs) ----------

    private static (string vendor, string raw, string model) Identify()
    {
        string manu = "", model = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    manu = (o["Manufacturer"] as string ?? "").Trim();
                    model = (o["Model"] as string ?? "").Trim();
                }
        }
        catch (Exception ex) { Config.Log("bios: זיהוי יצרן נכשל: " + ex.Message); }

        string low = manu.ToLowerInvariant();
        string vendor =
            low.Contains("lenovo") ? "lenovo" :
            (low.Contains("hewlett") || low.StartsWith("hp")) ? "hp" :
            low.Contains("dell") ? "dell" :
            (low.Contains("asus") || low.Contains("asustek")) ? "asus" :
            "other";
        return (vendor, manu, model);
    }

    // ---------- /bios/status: זיהוי + מה שזמין בפועל במחשב הזה, בלי לשנות כלום ----------

    /// <summary>הסטטוס של היצרן + מצב אתחול כללי (BitLocker/TPM/Secure Boot, ר' BootHardening) תחת "boot"</summary>
    private static object StatusWithBoot()
    {
        var node = JsonSerializer.SerializeToNode(Status(), Server.Json) as JsonObject ?? new JsonObject();
        try { node["boot"] = BootHardening.StatusNode(); }
        catch (Exception ex) { node["boot"] = new JsonObject { ["error"] = ex.Message }; }
        return node;
    }

    private static object Status()
    {
        var (vendor, raw, model) = Identify();
        switch (vendor)
        {
            case "lenovo":
            {
                var settings = LenovoSettings(out string err);
                var usb = settings.FirstOrDefault(s => LooksLikeUsbBootSetting(s.name));
                var bootMenu = settings.FirstOrDefault(s => LooksLikeBootMenuAuthSetting(s.name));
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = true,
                    wmiReachable = err == null,
                    wmiError = err,
                    usbBootSetting = usb.name == null ? null : new { name = usb.name, currentValue = usb.value, possibleValues = usb.possible },
                    bootMenuAuthSetting = bootMenu.name == null ? null : new { name = bootMenu.name, currentValue = bootMenu.value, possibleValues = bootMenu.possible },
                    note = "סדר הפעולות בהקשחה: קודם כל החסימות (USB/רשת/Legacy/סדר ותפריט אתחול), ורק בסוף קביעת הסיסמה - אחרי שנקבעת סיסמה, " +
                           "שינוי הגדרות בלי להזין אותה נכשל ב-Access Denied. אם כבר מוגדרת סיסמת Supervisor, יש להזין אותה כסיסמה נוכחית.",
                    supervisorPasswordSet = LenovoSupervisorSet(),
                };
            }
            case "hp":
            {
                var settings = HpSettings(out string err);
                var usb = settings.FirstOrDefault(s => LooksLikeUsbBootSetting(s.name));
                var bootMenu = settings.FirstOrDefault(s => LooksLikeBootMenuAuthSetting(s.name));
                var (pwIsSet, pwRequiresPhysical) = HpPasswordState();
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = true,
                    wmiReachable = err == null,
                    wmiError = err,
                    usbBootSetting = usb.name == null ? null : new { name = usb.name, currentValue = usb.value, possibleValues = usb.possible },
                    bootMenuAuthSetting = bootMenu.name == null ? null : new { name = bootMenu.name, currentValue = bootMenu.value, possibleValues = bootMenu.possible },
                    passwordIsSet = pwIsSet,
                    passwordRequiresPhysicalPresence = pwRequiresPhysical,
                    note = "נתמך בעיקר בדגמים עסקיים (EliteBook/EliteDesk/ProBook/Z). במחשבי צרכן (Pavilion/Envy וכו') ייתכן שהממשק אינו חשוף כלל. " +
                        (pwRequiresPhysical == true
                            ? "בדגם הזה HP מסמנת את סיסמת ה-BIOS כ-RequiresPhysicalPresence - לפי התיעוד הרשמי של HP, זה בעיקר אומר ששינוי בהגדרה " +
                              "כזו ממתין לאישור על המסך באתחול הבא (ולא בהכרח חסימה מוחלטת של הכתיבה עצמה). „הפעלת הקשחה” תנסה בכל זאת ותאמת מול " +
                              "התוצאה האמיתית (לא תדווח הצלחה בלי אישור); אם היא תדווח שהסיסמה עדיין לא נקבעה, הצעד הראשון הוא להפעיל מחדש ולבדוק אם מופיעה בקשת אישור במסך ה-BIOS."
                            : "לא זוהתה הגבלת נוכחות-פיזית על סיסמת ה-BIOS בדגם הזה, אבל זה לא מבטיח הצלחה - ר' תוצאת /bios/harden בפועל."),
                };
            }
            case "dell":
                if (DellCctkPath() != null)
                    return new
                    {
                        vendor,
                        manufacturer = raw,
                        model,
                        supported = true,
                        note = "Dell Command | Configure מותקן: אפשר להגדיר סיסמת BIOS אוטומטית (לא נבדק על דגם Dell אמיתי, ולכן מומלץ לוודא ב-F2 באתחול). " +
                               "חסימת UEFI Network Stack ו-Legacy ROM אוטומטית דרך cctk; חסימת אתחול USB ונעילת תפריט/סדר האתחול ב-Dell ידניות בלבד.",
                    };
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = false,
                    reason = "ל-Dell אין ממשק WMI מתועד להגדרת סיסמת BIOS בלי Dell Command | Configure (כלי חינמי של Dell). " +
                             "אם תתקינו אותו, הסיסמה תוגדר אוטומטית; אחרת — ידנית בתפריט ה-BIOS (F2 באתחול).",
                };
            case "asus":
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = false,
                    reason = "ל-Asus אין ממשק תוכנתי צרכני כלל. קיים \"ASUS Configuration Tool\" (ACT) רק למחשבים עסקיים מדור Intel Gen12 " +
                             "ומעלה (2022 ואילך) — לא רלוונטי לרוב המחשבים הביתיים. יש להגדיר ידנית בתפריט ה-BIOS (Del/F2 באתחול).",
                };
            default:
                return new
                {
                    vendor,
                    manufacturer = raw,
                    model,
                    supported = false,
                    reason = string.IsNullOrEmpty(raw)
                        ? "לא הצלחנו לזהות את יצרן המחשב."
                        : "היצרן (" + raw + ") אינו אחד היצרנים הנתמכים (Lenovo/HP) להקשחה אוטומטית.",
                };
        }
    }

    // ---------- /bios/harden: הפעולה בפועל (דורש סיסמת מנהל + 2FA, כמו FxPolicy.AdminGate) ----------

    internal const int MinBiosPassword = 12;

    /// <summary>
    /// בקשת הקשחה: חסימות (USB/רשת/Legacy/סדר אתחול/תפריט אתחול) ו/או קביעת סיסמה.
    /// WHY הסדר: אחרי שנקבעת סיסמת BIOS, כתיבת כל הגדרה אחרת בלי הסיסמה נכשלת ב-Access Denied (ב-HP/Lenovo/Dell).
    /// לכן החסימות קודם, והסיסמה תמיד אחרונה. אם כבר מוגדרת סיסמה, מעבירים אותה (currentBiosPassword) גם לחסימות.
    /// </summary>
    internal sealed class HardenRequest
    {
        public bool SetPassword, UsbBoot, NetworkBoot, Legacy, BootOrder, BootMenu;
        public bool Any => SetPassword || UsbBoot || NetworkBoot || Legacy || BootOrder || BootMenu;
    }

    /// <summary>סדר השלבים: כל החסימות לפי סדר קבוע, ואז סיסמה. טהור, ניתן לבדיקה</summary>
    internal static List<string> StageOrder(HardenRequest r)
    {
        var l = new List<string>();
        if (r.UsbBoot) l.Add("usbBoot");
        if (r.NetworkBoot) l.Add("networkBoot");
        if (r.Legacy) l.Add("legacy");
        if (r.BootOrder) l.Add("bootOrder");
        if (r.BootMenu) l.Add("bootMenuAuth");
        if (r.SetPassword) l.Add("password");
        return l;
    }

    /// <summary>null אם תקינה, אחרת הסיבה לדחייה</summary>
    internal static string ValidateBiosPassword(string pw) =>
        (pw ?? "").Length < MinBiosPassword ? "סיסמת ה-BIOS חייבת להכיל לפחות " + MinBiosPassword + " תווים" : null;

    private static object Harden(JsonElement b)
    {
        if (!Kiosk.PasswordConfigured)
            throw new BridgeException("no_password", "יש להגדיר סיסמת נעילה לפני ניסיון הקשחת BIOS", 400);
        string pw = b.Str("password");
        Kiosk.RequirePasswordOrRecovery(pw);
        if (!Kiosk.IsRecoveryCode(pw)) TwoFactor.Require("הקשחת BIOS/UEFI");

        bool all = b.Bool("all");
        var req = new HardenRequest
        {
            SetPassword = b.Bool("setPassword") || all,
            UsbBoot = b.Bool("blockUsbBoot") || all,
            NetworkBoot = b.Bool("blockNetworkBoot") || all,
            Legacy = b.Bool("blockLegacy") || all,
            BootOrder = b.Bool("lockBootOrder") || all,
            BootMenu = b.Bool("lockBootMenu") || all,
        };
        string newBios = b.Str("biosPassword") ?? "";
        string curBios = b.Str("currentBiosPassword") ?? "";
        if (!req.Any)
            throw BridgeException.BadRequest("יש לבחור לפחות פעולה אחת: חסימת אתחול (USB/רשת/Legacy), נעילת סדר/תפריט אתחול, או הגדרת סיסמת BIOS");
        if (req.SetPassword && ValidateBiosPassword(newBios) is string bad)
            throw BridgeException.BadRequest(bad);

        var (vendor, raw, model) = Identify();
        bool dellOk = vendor == "dell" && DellCctkPath() != null;
        if (vendor is not ("lenovo" or "hp") && !dellOk)
            throw BridgeException.Unsupported("היצרן (" + (string.IsNullOrEmpty(raw) ? "לא זוהה" : raw) +
                ") אינו נתמך להקשחה אוטומטית. ר' /bios/status להסבר ולאפשרויות הידניות");

        var steps = new List<object>();
        var failedIds = new List<string>();
        void Add(string id, (bool attempted, bool success, string settingName, string message) r)
        {
            steps.Add(new { id, attempted = r.attempted, success = r.success, settingName = r.settingName, message = r.message });
            if (!r.success) failedIds.Add(id);
            Config.Log("bios harden: " + id + " vendor=" + vendor + " setting=" + r.settingName + " success=" + r.success + " msg=" + r.message);
        }

        foreach (string stage in StageOrder(req))
        {
            if (stage == "password")
            {
                var r = dellOk ? DellSetPassword(newBios, curBios) : vendor == "lenovo" ? LenovoSetPassword(newBios, curBios) : HpSetPassword(newBios, curBios);
                Add("password", (r.attempted, r.success, null, r.message));
            }
            else if (dellOk) Add(stage, DellApplyStage(stage, curBios));
            else Add(stage, vendor == "lenovo" ? LenovoApplyStage(stage, curBios) : HpApplyStage(stage, curBios));
        }

        // כשל בשלב אינו שקט: הקשחה חלקית נראית חלקית, והמנהל מקבל הוראה ברורה
        bool failed = failedIds.Count > 0;
        string summary = failed
            ? "הקשחה לא הושלמה: " + string.Join(", ", failedIds) +
              ". יש לטפל ידנית ב-BIOS או לנסות שוב (אם כבר מוגדרת סיסמת BIOS, יש להזין אותה בשדה הסיסמה הנוכחית)."
            : "ההקשחה הושלמה (השינויים נכנסים לתוקף באתחול הבא)";
        if (failed) Config.Log("bios harden: " + summary);
        return new { vendor, manufacturer = raw, model, complete = !failed, summary, steps };
    }

    // ---------- זיהוי הגדרת "אתחול מ-USB" בלי להניח שם קבוע (משתנה בין דגמים) ----------

    private static readonly string[] UsbBootPhrases =
    {
        "usb boot", "boot from external", "external device boot", "boot from usb",
        "usb storage boot", "legacy usb boot", "boot external devices", "external boot device",
    };

    internal static bool LooksLikeUsbBootSetting(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        if (UsbBootPhrases.Any(n.Contains)) return true;
        bool hasUsb = n.Contains("usb");
        bool hasExternal = n.Contains("external");
        bool hasBoot = n.Contains("boot");
        return (hasUsb && hasBoot) || (hasExternal && hasBoot);
    }

    /// <summary>
    /// אתחול מהרשת: PXE / UEFI Network Stack / Boot from LAN. מוציאים Wake-on-LAN/WLAN/Bluetooth: אלה לא אתחול מהרשת,
    /// וכיבוי שלהם עלול לפגוע בניהול (WoL) בלי להועיל. "network" לבדו לא מספיק: חייב גם boot/stack/pxe.
    /// </summary>
    internal static bool LooksLikeNetworkBootSetting(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        if (n.Contains("wake") || n.Contains("wol") || n.Contains("wlan") || n.Contains("wifi") || n.Contains("wi-fi") || n.Contains("bluetooth")) return false;
        if (n.Contains("pxe") || n.Contains("network stack") || n.Contains("netstack")) return true;
        bool net = n.Contains("network") || System.Text.RegularExpressions.Regex.IsMatch(n, @"\blan\b");
        return net && (n.Contains("boot") || n.Contains("stack"));
    }

    /// <summary>
    /// Legacy/CSM: מאפשרים אתחול ללא Secure Boot ואתחול OROM ישן. לא נוגעים ב"Legacy USB Support" (מקלדת USB לפני אתחול;
    /// כיבויו עלול לנעול את המנהל מחוץ ל-BIOS); כל שם עם "usb" מוחרג כאן.
    /// </summary>
    internal static bool LooksLikeLegacySetting(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        if (n.Contains("usb")) return false;
        return n.Contains("csm") || n.Contains("compatibility support") || n.Contains("legacy boot") || n.Contains("legacy support")
            || n.Contains("legacy mode") || n.Contains("legacy option rom") || n.Contains("legacy rom");
    }

    /// <summary>נעילת סדר האתחול (מניעת שינוי סדר/הוספת התקן): הגדרה שמכילה "boot order" יחד עם lock/prevent/protect</summary>
    internal static bool LooksLikeBootOrderLockSetting(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        return n.Contains("boot order") && (n.Contains("lock") || n.Contains("prevent") || n.Contains("protect") || n.Contains("disallow"));
    }

    /// <summary>
    /// הגדרה שדורשת סיסמת מנהל כדי בכלל להיכנס לתפריט האתחול (F9/F12 וכו') - נמצאה בפועל על HP EliteBook
    /// 840 G8 בשם "Prompt for Admin authentication on F9 (Boot Menu)". חסימה חזקה יותר מחסימת USB בלבד:
    /// סוגרת גישה לכל תפריט האתחול, לא רק ל-USB. שם ההגדרה משתנה בין דגמים, אז חיפוש גמיש גם כאן.
    /// </summary>
    private static readonly string[] BootMenuAuthPhrases =
    {
        "admin authentication on f9", "admin authentication on f12", "boot menu authentication",
        "require password for boot menu", "password for f9", "password for f12",
    };

    internal static bool LooksLikeBootMenuAuthSetting(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        if (BootMenuAuthPhrases.Any(n.Contains)) return true;
        bool hasBootMenu = n.Contains("boot menu") || n.Contains("f9") || n.Contains("f12");
        bool hasAuth = n.Contains("admin") || n.Contains("authentication") || n.Contains("password");
        return hasBootMenu && hasAuth;
    }

    /// <summary>ערכים שמשמעותם "חסום/כבוי". התאמה מדויקת בלבד</summary>
    private static readonly string[] DisableWords = { "disable", "disabled", "off", "no", "block", "blocked" };
    /// <summary>ערכים שמשמעותם "מופעל" (דורש סיסמה/נעול). התאמה מדויקת בלבד</summary>
    private static readonly string[] EnableWords = { "enable", "enabled", "on", "yes", "require", "required", "prompt", "lock", "locked" };

    /// <summary>
    /// בוחר ערך אפשרי לפי התאמה מדויקת (אחרי trim, הורדת כוכבית ואותיות קטנות), לא Contains:
    /// WHY: Contains על "no" בחר בטעות את "Normal"/"Nothing" כ-disable. מחזיר את הערך כפי שמופיע ב-firmware.
    /// </summary>
    internal static string PickValue(string[] possible, string[] words)
    {
        if (possible == null) return null;
        foreach (string w in words)
            foreach (string p in possible)
            {
                string clean = (p ?? "").Trim().TrimStart('*').Trim();
                if (string.Equals(clean, w, StringComparison.OrdinalIgnoreCase)) return clean;
            }
        return null;
    }

    internal static string PickDisableValue(string[] possible) => PickValue(possible, DisableWords);
    internal static string PickEnableValue(string[] possible) => PickValue(possible, EnableWords);

    internal sealed class StageSpecInfo
    {
        public Func<string, bool> Matcher;
        public Func<string[], string> Picker;
        public string NotFound, Label;
    }

    /// <summary>לכל שלב: אילו שמות הגדרות רלוונטיים ואיזה ערך לבחור</summary>
    internal static StageSpecInfo StageSpec(string stage) => stage switch
    {
        "usbBoot" => new StageSpecInfo { Matcher = LooksLikeUsbBootSetting, Picker = PickDisableValue, Label = "חוסם אתחול USB",
            NotFound = "לא נמצאה הגדרת BIOS לאתחול מהתקן USB/חיצוני. ייתכן שהדגם קורא לה בשם אחר או שאינה חשופה - יש לבדוק ידנית בתפריט ה-BIOS" },
        "networkBoot" => new StageSpecInfo { Matcher = LooksLikeNetworkBootSetting, Picker = PickDisableValue, Label = "חוסם אתחול רשת",
            NotFound = "לא נמצאה הגדרת BIOS לאתחול מהרשת (PXE / UEFI Network Stack). יש לבדוק ידנית בתפריט ה-BIOS" },
        "legacy" => new StageSpecInfo { Matcher = LooksLikeLegacySetting, Picker = PickDisableValue, Label = "מכבה Legacy/CSM",
            NotFound = "לא נמצאה הגדרת Legacy/CSM. ייתכן שהדגם UEFI בלבד (אז אין מה לכבות), או שיש לבדוק ידנית בתפריט ה-BIOS" },
        "bootOrder" => new StageSpecInfo { Matcher = LooksLikeBootOrderLockSetting, Picker = PickEnableValue, Label = "נועל סדר אתחול",
            NotFound = "לא נמצאה הגדרת נעילת סדר האתחול. יש לבדוק ידנית בתפריט ה-BIOS" },
        "bootMenuAuth" => new StageSpecInfo { Matcher = LooksLikeBootMenuAuthSetting, Picker = PickEnableValue, Label = "דורש סיסמה לתפריט אתחול",
            NotFound = "לא נמצאה הגדרה שדורשת סיסמת מנהל לתפריט האתחול (F9/F12). אומת בפועל ש-HP EliteBook 840 G8 חושפת הגדרה כזו תחת \"Prompt for Admin authentication on F9 (Boot Menu)\"" },
        _ => throw new ArgumentException("שלב לא מוכר: " + stage),
    };

    /// <summary>
    /// תכנון (טהור): עבור שלב והרשימה הנוכחית, אילו הגדרות לשנות ולאיזה ערך. מטפל בכל ההגדרות התואמות (למשל IPv4+IPv6 PXE),
    /// לא רק בראשונה. הגדרה שכבר בערך הרצוי מסומנת alreadyOk; הגדרה בלי ערך מזוהה: newValue=null עם unknownMsg.
    /// </summary>
    internal static List<(string name, string newValue, bool alreadyOk, string unknownMsg)> PlanStage(string stage, IEnumerable<(string name, string value, string[] possible)> settings)
    {
        var spec = StageSpec(stage);
        var list = new List<(string, string, bool, string)>();
        foreach (var s in settings.Where(x => spec.Matcher(x.name)))
        {
            string nv = spec.Picker(s.possible);
            if (nv == null)
                list.Add((s.name, null, false, "נמצאה הגדרה \"" + s.name + "\" אך לא ניתן לזהות איזה מהערכים (" + string.Join(", ", s.possible ?? Array.Empty<string>()) + ") " + spec.Label + " - יש להגדיר ידנית"));
            else
            {
                string v = s.value ?? "";
                string cur = v.Split(',').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("*"))?.TrimStart('*').Trim() ?? v.Trim();
                list.Add((s.name, nv, string.Equals(cur, nv, StringComparison.OrdinalIgnoreCase), null));
            }
        }
        return list;
    }

    /// <summary>איחוד תוצאות של כמה הגדרות בשלב אחד: הצלחה רק אם כולן הצליחו; ללא הגדרות כלל = כשל עם הודעת "לא נמצא"</summary>
    internal static (bool attempted, bool success, string settingName, string message) Combine(
        string notFound, List<(bool attempted, bool success, string name, string message)> parts)
    {
        if (parts.Count == 0) return (false, false, null, notFound);
        bool ok = parts.All(p => p.success);
        return (parts.Any(p => p.attempted), ok, string.Join(", ", parts.Select(p => p.name)),
            string.Join(" | ", parts.Select(p => p.message)));
    }

    // ---------- Lenovo: root\wmi ----------

    /// <summary>כל הגדרות ה-BIOS הנוכחיות (Lenovo_BiosSetting.CurrentSetting), כל שורה בתבנית "שם,ערך,[אפשרות1,אפשרות2,...]"</summary>
    private static List<(string name, string value, string[] possible)> LenovoSettings(out string error)
    {
        error = null;
        var list = new List<(string, string, string[])>();
        try
        {
            // לפי התיעוד של Lenovo: כל מופע של Lenovo_BiosSetting הוא הגדרה אחת, ו-CurrentSetting הוא *מאפיין* (property,
            // מחרוזת "שם,ערך;[Optional:...]") ולא מתודה. קודם נקרא InvokeMethod על מופע ראשון בלבד (לא נבדק על מכשיר
            // Lenovo אמיתי - אין כזה במחשב הפיתוח - ולכן ההודעות למשתמש מציינות שהאימות הוא לפי קריאה חוזרת)
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentSetting FROM Lenovo_BiosSetting");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var parsed = ParseLenovoLine(o["CurrentSetting"] as string);
                    if (parsed.name != null) list.Add(parsed);
                }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Config.Log("bios lenovo enumerate: " + ex.Message);
        }
        return list;
    }

    internal static (string name, string value, string[] possible) ParseLenovoLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (null, null, null);
        // פורמטים: "שם,ערך;[Optional:א,ב,ג]" או "שם,ערך,[א,ב,ג]" - הראש נגמר ב-';' או ב-'[' הראשון
        int cut = line.IndexOfAny(new[] { ';', '[' });
        string head = cut >= 0 ? line[..cut].TrimEnd(',', ' ') : line;
        string[] parts = head.Split(',');
        if (parts.Length < 2) return (null, null, null);
        string name = parts[0].Trim();
        string value = parts[1].Trim();
        string[] possible = Array.Empty<string>();
        if (cut >= 0)
        {
            string inner = line[cut..].Trim('[', ']', ';', ' ');
            if (inner.StartsWith("Optional:", StringComparison.OrdinalIgnoreCase)) inner = inner["Optional:".Length..];
            possible = inner.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }
        return (name.Length == 0 ? null : name, value, possible);
    }

    /// <summary>
    /// מצב סיסמת Supervisor לפי Lenovo_BiosPasswordSettings.PasswordState (מסכת ביטים: 2 = Supervisor מוגדרת).
    /// null אם לא ניתן לקרוא. לא נבדק על ThinkPad אמיתי (אין כזה בסביבת הפיתוח).
    /// </summary>
    private static bool? LenovoSupervisorSet()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT PasswordState FROM Lenovo_BiosPasswordSettings");
            foreach (ManagementObject o in s.Get())
                using (o) return (Convert.ToInt32(o["PasswordState"]) & 2) != 0;
        }
        catch (Exception ex) { Config.Log("bios lenovo password state: " + ex.Message); }
        return null;
    }

    /// <summary>סיומת הסיסמה הנוכחית לפרמטרים של Lenovo: ",pwd,ascii,us" כשמוגדרת סיסמה, אחרת ריק</summary>
    internal static string LenovoPwdSuffix(string currentPassword) =>
        string.IsNullOrEmpty(currentPassword) ? "" : "," + currentPassword + ",ascii,us";

    private static (bool attempted, bool success, string message) LenovoSetPassword(string newBiosPassword, string currentPassword)
    {
        try
        {
            using var sp = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM Lenovo_SetBiosPassword");
            foreach (ManagementObject o in sp.Get())
            {
                using (o)
                {
                    // "pap" = סיסמת Supervisor/Admin (שולטת גם בהגדרות אתחול). הפרמטר השני הוא הסיסמה הנוכחית (ריק = אין).
                    // לא דורסים סיסמה קיימת שלא ידועה לנו: הקריאה פשוט נכשלת
                    string parameter = "pap," + (currentPassword ?? "") + "," + newBiosPassword + ",ascii,us";
                    string text = LenovoCall(o, "SetBiosPassword", parameter);
                    bool ok = text.Equals("Success", StringComparison.OrdinalIgnoreCase);
                    if (!ok)
                        return (true, false, "הגדרת סיסמת ה-BIOS נכשלה: " + (string.IsNullOrEmpty(text) ? "תשובה לא ידועה מה-firmware" : text) +
                              " — ייתכן שכבר מוגדרת סיסמה אחרת (יש להזין אותה כסיסמה נוכחית), או שהדגם אינו תומך בפרמטר הזה");
                    // אימות מצב (לא רק "Success"): הסיסמה אמורה להופיע כמוגדרת
                    bool? set = LenovoSupervisorSet();
                    if (set == false) return (true, false, "ה-firmware החזיר Success אך הסיסמה לא מופיעה כמוגדרת (PasswordState). יש לוודא ידנית בתפריט ה-BIOS");
                    return (true, true, set == true ? "סיסמת ה-BIOS הוגדרה ואומתה (PasswordState)" : "סיסמת ה-BIOS הוגדרה (לא ניתן היה לאמת מול PasswordState - מומלץ לבדוק ידנית)");
                }
            }
            return (false, false, "לא נמצא ממשק Lenovo_SetBiosPassword במחשב זה");
        }
        catch (Exception ex)
        {
            Config.Log("bios lenovo set-password: " + ex.Message);
            return (true, false, "הגדרת סיסמת ה-BIOS נכשלה: " + ex.Message);
        }
    }

    /// <summary>שלב חסימה אחד ב-Lenovo: כל ההגדרות התואמות (לא רק הראשונה), כל אחת נכתבת ונשמרת ומאומתת בקריאה חוזרת</summary>
    private static (bool attempted, bool success, string settingName, string message) LenovoApplyStage(string stage, string currentPassword)
    {
        var spec = StageSpec(stage);
        var settings = LenovoSettings(out string err);
        if (err != null) return (false, false, null, "קריאת הגדרות ה-BIOS נכשלה: " + err);
        var plan = PlanStage(stage, settings);
        var parts = new List<(bool attempted, bool success, string name, string message)>();
        foreach (var p in plan)
        {
            if (p.newValue == null) { parts.Add((false, false, p.name, p.unknownMsg)); continue; }
            if (p.alreadyOk) { parts.Add((false, true, p.name, "\"" + p.name + "\" כבר \"" + p.newValue + "\"")); continue; }
            var r = LenovoWriteSetting(p.name, p.newValue, currentPassword);
            parts.Add((r.attempted, r.success, p.name, r.message));
        }
        return Combine(spec.NotFound, parts);
    }

    private static (bool attempted, bool success, string message) LenovoWriteSetting(string name, string newValue, string currentPassword)
    {
        try
        {
            string suffix = LenovoPwdSuffix(currentPassword);
            string setText = "";
            using (var setS = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM Lenovo_SetBiosSetting"))
                foreach (ManagementObject o in setS.Get())
                {
                    using (o) { setText = LenovoCall(o, "SetBiosSetting", name + "," + newValue + suffix); }
                    break;
                }
            if (!setText.Equals("Success", StringComparison.OrdinalIgnoreCase))
                return (true, false, "שינוי ההגדרה \"" + name + "\" נכשל: " + (string.IsNullOrEmpty(setText) ? "תשובה לא ידועה" : setText) +
                    " — אם כבר מוגדרת סיסמת Supervisor יש להזין אותה כסיסמה נוכחית (Access Denied אחרת)");

            string saveText = "";
            string saveParam = string.IsNullOrEmpty(currentPassword) ? "" : currentPassword + ",ascii,us";
            using (var saveS = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM Lenovo_SaveBiosSettings"))
                foreach (ManagementObject o in saveS.Get())
                {
                    using (o) { saveText = LenovoCall(o, "SaveBiosSettings", saveParam); }
                    break;
                }
            if (!saveText.Equals("Success", StringComparison.OrdinalIgnoreCase))
                return (true, false, "ההגדרה \"" + name + "\" שונתה אבל השמירה (SaveBiosSettings) נכשלה: " + saveText);

            // אימות בקריאה חוזרת: ללא זה "Success" לא מוכיח שהערך באמת השתנה
            var now = LenovoSettings(out _).FirstOrDefault(x => x.name == name);
            if (now.name != null && !string.Equals(now.value, newValue, StringComparison.OrdinalIgnoreCase))
                return (true, false, "ההגדרה \"" + name + "\" נשמרה אך הערך עדיין \"" + now.value + "\" (נדרש \"" + newValue + "\") - לא אומת");
            return (true, true, "\"" + name + "\" הוגדרה ל-\"" + newValue + "\" ונשמרה. תיכנס לתוקף מההפעלה הבאה");
        }
        catch (Exception ex)
        {
            Config.Log("bios lenovo apply-setting: " + ex.Message);
            return (true, false, "שינוי ההגדרה \"" + name + "\" נכשל: " + ex.Message);
        }
    }

    /// <summary>קריאה למתודת Lenovo: התוצאה ("Success"/שגיאה) חוזרת בפרמטר הפלט "return", לא כערך החזרה רגיל</summary>
    private static string LenovoCall(ManagementObject o, string method, string parameter)
    {
        using var inp = o.GetMethodParameters(method);
        inp["parameter"] = parameter;
        using var outp = o.InvokeMethod(method, inp, null);
        return outp?["return"] as string ?? "";
    }

    // ---------- Dell: Dell Command | Configure (cctk.exe) - רק אם כבר מותקן; אין ל-Dell ממשק WMI מתועד בלעדיו ----------

    private static string DellCctkPath()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            foreach (var sub in new[] { @"Dell\Command Configure\X86_64\cctk.exe", @"Dell\Command Configure\X86\cctk.exe" })
            {
                string p = Path.Combine(root, sub);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>
    /// אפשרויות cctk לכל שלב (לא נבדק על Dell אמיתי). uefinetworkstack ו-legacyorom הם שמות האפשרויות ב-cctk; אין ל-Dell אפשרות
    /// מתועדת לתפריט אתחול/סדר אתחול נעול, ולכן אלה נשארים ידניים.
    /// </summary>
    internal static string DellOptionForStage(string stage) => stage switch
    {
        "networkBoot" => "uefinetworkstack=disabled",
        "legacy" => "legacyorom=disabled",
        _ => null,
    };

    /// <summary>
    /// הפקודה של cctk: אפשרות רגילה בשורת הפקודה, אבל כל מה שמכיל סיסמה עובר בקובץ זמני (-i) כדי שלא יופיע בשורת
    /// הפקודה של התהליך (נראית לכל משתמש ב-Task Manager/WMI). הקובץ נכתב עם ACL שמגביל ליוצר+SYSTEM+Administrators,
    /// ובסיום נדרס באפסים ונמחק. פורמט הקובץ (שורה לכל אפשרות, בלי "--") לפי התיעוד של cctk; לא נבדק על Dell אמיתי.
    /// </summary>
    private static string DellRun(string cctk, IEnumerable<string> options, bool hasSecret)
    {
        var opts = options.ToList();
        if (!hasSecret) return Config.Run(cctk, string.Join(" ", opts.Select(o => "--" + o)), 30000).Trim();

        string dir = Path.Combine(HardLock.DataDir, "tmp");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".cctk");
        try
        {
            var fs = new System.Security.AccessControl.FileSecurity();
            fs.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { System.Security.Principal.WindowsIdentity.GetCurrent().User,
                                        new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                                        new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null) })
                fs.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            using (var f = System.IO.FileSystemAclExtensions.Create(new FileInfo(file), FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, fs))
            using (var w = new StreamWriter(f, new System.Text.UTF8Encoding(false)))
                foreach (string o in opts) w.WriteLine(o);
            return Config.Run(cctk, "-i \"" + file + "\"", 30000).Trim();
        }
        finally
        {
            try { long len = new FileInfo(file).Length; File.WriteAllBytes(file, new byte[Math.Max(len, 1)]); File.Delete(file); }
            catch (Exception ex) { Config.Log("bios dell temp cleanup: " + ex.Message); }
        }
    }

    internal static bool DellOutputOk(string text) =>
        text.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0 &&
        text.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("fail", StringComparison.OrdinalIgnoreCase) < 0;

    /// <summary>קריאת ערך נוכחי של אפשרות ב-cctk ("uefinetworkstack=Disabled" → "Disabled")</summary>
    internal static string DellParseValue(string text, string option)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"^\s*" + System.Text.RegularExpressions.Regex.Escape(option) + @"\s*=\s*(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static (bool attempted, bool success, string settingName, string message) DellApplyStage(string stage, string currentPassword)
    {
        string cctk = DellCctkPath();
        if (cctk == null) return (false, false, null, "Dell Command | Configure אינו מותקן במחשב זה");
        string opt = DellOptionForStage(stage);
        if (opt == null)
            return (false, false, null, "ב-Dell השלב \"" + stage + "\" מוגדר ידנית ב-BIOS (F2 באתחול) - אין אפשרות cctk מתועדת");
        try
        {
            string name = opt.Split('=')[0], want = opt.Split('=')[1];
            var options = new List<string>();
            bool secret = !string.IsNullOrEmpty(currentPassword);
            if (secret) options.Add("valsetuppwd=" + currentPassword); // סיסמה נוכחית, בקובץ בלבד
            options.Add(opt);
            string text = DellRun(cctk, options, secret);
            Config.Log("bios dell cctk " + name + ": " + text.Replace(currentPassword ?? "\u0001", "***"));
            // אימות: קריאה חוזרת של האפשרות
            string back = DellParseValue(Config.Run(cctk, "--" + name, 30000), name);
            bool ok = back != null && string.Equals(back, want, StringComparison.OrdinalIgnoreCase);
            if (back == null) ok = DellOutputOk(text); // אין קריאה חוזרת: נשענים על פלט הפקודה ומציינים
            return (true, ok, name, ok ? name + " הוגדר ל-" + want + (back == null ? " (לא אומת בקריאה חוזרת; לא נבדק על Dell אמיתי)" : " (אומת)")
                                       : name + " לא הוגדר: " + (text.Length == 0 ? "אין פלט" : text.Replace(currentPassword ?? "\u0001", "***")));
        }
        catch (Exception ex)
        {
            Config.Log("bios dell apply: " + ex.Message);
            return (true, false, null, "הרצת cctk נכשלה: " + ex.Message);
        }
    }

    /// <summary>
    /// לא נבדק על מחשב Dell אמיתי: סיסמה בקובץ זמני מוגן (לא בשורת הפקודה), הצלחה רק אם הפלט מכיל "success" ואין שגיאה,
    /// ובכל מקרה ממליץ לאמת בכניסה ל-BIOS (F2 באתחול)
    /// </summary>
    private static (bool attempted, bool success, string message) DellSetPassword(string newBiosPassword, string currentPassword)
    {
        string cctk = DellCctkPath();
        if (cctk == null) return (false, false, "Dell Command | Configure אינו מותקן במחשב זה");
        if (!System.Text.RegularExpressions.Regex.IsMatch(newBiosPassword, @"^[A-Za-z0-9!@#_.\-]{" + MinBiosPassword + @",32}$"))
            return (false, false, "ב-Dell נתמכות רק אותיות באנגלית, ספרות והתווים ! @ # _ . - (" + MinBiosPassword + " עד 32 תווים)");
        try
        {
            var options = new List<string>();
            if (!string.IsNullOrEmpty(currentPassword)) options.Add("valsetuppwd=" + currentPassword);
            options.Add("setuppwd=" + newBiosPassword);
            string text = DellRun(cctk, options, true);
            string safe = text.Replace(newBiosPassword, "***");
            if (!string.IsNullOrEmpty(currentPassword)) safe = safe.Replace(currentPassword, "***");
            Config.Log("bios dell cctk: " + safe);
            bool ok = DellOutputOk(text);
            return (true, ok, ok
                ? "cctk דיווח הצלחה. מומלץ לוודא בכניסה ל-BIOS (F2 באתחול) - הפעולה לא נבדקה על דגם Dell אמיתי"
                : "cctk לא דיווח הצלחה: " + (safe.Length == 0 ? "אין פלט" : safe) + " — אפשר להגדיר ידנית ב-BIOS (F2 באתחול)");
        }
        catch (Exception ex)
        {
            Config.Log("bios dell set-password: " + ex.Message);
            return (true, false, "הרצת cctk נכשלה: " + ex.Message);
        }
    }

    // ---------- HP: root\hp\instrumentedBIOS ----------

    private static List<(string name, string value, string[] possible)> HpSettings(out string error)
    {
        error = null;
        var list = new List<(string, string, string[])>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT Name, Value, PossibleValues FROM HP_BIOSEnumeration");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    string name = o["Name"] as string ?? "";
                    string value = o["Value"] as string ?? "";
                    string[] possible = (o["PossibleValues"] as string[]) ?? Array.Empty<string>();
                    if (name.Length > 0) list.Add((name, value, possible));
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Config.Log("bios hp enumerate: " + ex.Message);
        }
        return list;
    }

    private static string HpErrorText(uint code) => code switch
    {
        1 => "לא נתמך בדגם זה",
        2 => "שגיאה לא ידועה",
        3 => "תם הזמן הקצוב",
        4 => "הפעולה נכשלה",
        5 => "פרמטר לא תקין (ייתכן שם הגדרה שגוי)",
        6 => "הגישה נדחתה (ייתכן שסיסמת BIOS אחרת כבר מוגדרת)",
        _ => "קוד תשובה לא מוכר (" + code + ")",
    };

    /// <summary>
    /// true/false/null (לא ידוע) אם "Setup Password" כבר מוגדרת בפועל, לפי HP_BIOSPassword.IsSet - מקור
    /// האמת היחיד (בניגוד לקוד התשובה מ-SetBIOSSetting, שעלול לדווח "הצלחה" גם כשכלום לא קרה בפועל -
    /// ר' ההערה ב-HpSetPassword). גם RequiresPhysicalPresence זמין כאן למי שרוצה אותו למידע בלבד.
    /// </summary>
    private static (bool? isSet, bool? requiresPhysicalPresence) HpPasswordState()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT IsSet, RequiresPhysicalPresence FROM HP_BIOSPassword WHERE Name=\"Setup Password\"");
            foreach (ManagementObject o in s.Get())
                using (o)
                    return (Convert.ToInt32(o["IsSet"]) != 0, Convert.ToInt32(o["RequiresPhysicalPresence"]) != 0);
        }
        catch (Exception ex) { Config.Log("bios hp password state: " + ex.Message); }
        return (null, null);
    }

    /// <summary>
    /// מנסה תמיד בפועל (לא מוותרים מראש על בסיס RequiresPhysicalPresence בלבד - יש דגמים/גרסאות
    /// firmware שבהם זה בסך הכול תיאור של ההגדרה ולא חסימה מוחלטת של הכתיבה), ומוודא את התוצאה
    /// האמיתית מול HP_BIOSPassword.IsSet אחרי הקריאה - כי קוד התשובה של SetBIOSSetting לבדו לא אמין:
    /// אומת בפועל על HP EliteBook 840 G8 שהקריאה יכולה "להצליח" (קוד 0) בעוד IsSet נשאר 0.
    /// </summary>
    /// <summary>ארגומנט הסיסמה הנוכחית ל-HP: ריק אם אין; אחרת בקידוד Unicode (&lt;utf-16/&gt;)</summary>
    internal static string HpPwdArg(string pw) => string.IsNullOrEmpty(pw) ? "" : "<utf-16/>" + pw;

    private static (bool attempted, bool success, string message) HpSetPassword(string newBiosPassword, string currentPassword)
    {
        var (wasSet, requiresPhysical) = HpPasswordState();
        try
        {
            // ManagementClass(...).GetInstances() החזיר ריק אצל כמה משתמשים גם כש-root\hp\instrumentedBIOS
            // בהחלט זמין (ר' HpSettings למטה, שקוראת מאותו namespace בהצלחה) - ManagementObjectSearcher עם
            // שאילתת WQL הוא הדפוס שכבר מוכח עובד כאן, אז משתמשים באותו דפוס גם לכתיבה
            using var s = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT * FROM HP_BIOSSettingInterface");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    // סדר הפרמטרים האמיתי של HP_BIOSSettingInterface.SetBIOSSetting הוא
                    // (Name, Password-נוכחית, Value-חדש) - לא (Name, Value, Password) כפי שהיה כאן בטעות
                    // (אומת מול הגדרת המחלקה: Get-CimClass ...| select CimClassMethods['SetBIOSSetting'].Parameters).
                    // סיסמה חדשה בקידוד Unicode עוטפים ב-"<utf-16/>"; בלי סיסמה נוכחית מוגדרת משאירים ריק —
                    // אם כבר מוגדרת סיסמה אחרת (לא ידועה לנו) הקריאה נכשלת (Access Denied) ולא דורסת אותה
                    uint? code = HpSetCall(o, "Setup Password", HpPwdArg(currentPassword), "<utf-16/>" + newBiosPassword);
                    if (code != 0)
                        return (true, false, code == null
                            ? "הגדרת סיסמת ה-BIOS נכשלה: ה-BIOS לא החזיר קוד תשובה"
                            : "הגדרת סיסמת ה-BIOS נכשלה (קוד " + code + "): " + HpErrorText(code.Value) +
                              " — ייתכן שכבר מוגדרת סיסמה אחרת (לא נדרסת בלי לדעת אותה)");
                    // קוד 0 לבדו לא מספיק - מוודאים מול IsSet שבאמת קרה משהו (ר' הערת המתודה)
                    var (nowSet, _) = HpPasswordState();
                    if (nowSet == true) return (true, true, "סיסמת ה-BIOS הוגדרה בהצלחה");
                    if (nowSet == false)
                        return (true, false, "הקריאה החזירה קוד הצלחה, אבל הסיסמה עדיין לא מופיעה כנקבעת (אומת מול HP_BIOSPassword.IsSet)." +
                            (requiresPhysical == true
                                ? " בדגם הזה מסומן RequiresPhysicalPresence על ההגדרה - לפי התיעוד הרשמי של HP, ברוב המקרים זה "
                                  + "אומר שהשינוי רק *ממתין לאישור על המסך באתחול הבא* (לא בהכרח חסימה מוחלטת של הכתיבה). "
                                  + "כדאי קודם פשוט להפעיל מחדש את המחשב ולעקוב אחרי מסך ה-BIOS בתחילת האתחול — אם מופיעה שם בקשת אישור, יש לאשר אותה, ואז הסיסמה אמורה להיקבע בפועל. "
                                  + "אם אחרי אתחול כזה הבדיקה עדיין מראה שהסיסמה לא נקבעה, רק אז יש להגדיר אותה ידנית: כניסה ל-BIOS (F10 באתחול) ← Security ← Setup Password."
                                : ". יש להגדיר ידנית: כניסה ל-BIOS (F10 באתחול) ← Security ← Setup Password."));
                    // לא הצלחנו לקרוא IsSet בכלל (nowSet == null) - מדווחים לפי קוד התשובה בלבד, בלי להמציא ודאות
                    return (true, true, "סיסמת ה-BIOS הוגדרה כנראה בהצלחה (קוד 0) - לא ניתן היה לאמת מול IsSet, מומלץ לבדוק ב-F10");
                }
            }
            return (false, false, "לא נמצא ממשק HP_BIOSSettingInterface במחשב זה");
        }
        catch (Exception ex)
        {
            Config.Log("bios hp set-password: " + ex.Message);
            return (true, false, "הגדרת סיסמת ה-BIOS נכשלה: " + ex.Message);
        }
    }

    /// <summary>
    /// קריאה נכונה ל-HP_BIOSSettingInterface.SetBIOSSetting: על מופע (instance) עם פרמטרים בשם, ואת התוצאה קוראים
    /// מפרמטר הפלט Return (הוא לא ערך החזרה רגיל, ולכן InvokeMethod(שם, object[]) החזיר null). אומת ידנית:
    /// Invoke-CimMethod על מופע החזיר Return=0 והערך השתנה בפועל
    /// </summary>
    private static uint? HpSetCall(ManagementObject instance, string name, string currentPassword, string value)
    {
        using var inp = instance.GetMethodParameters("SetBIOSSetting");
        inp["Name"] = name;
        inp["Password"] = currentPassword;
        inp["Value"] = value;
        using var outp = instance.InvokeMethod("SetBIOSSetting", inp, null);
        object r = outp?["Return"];
        return r == null ? null : Convert.ToUInt32(r);
    }

    /// <summary>הערך הנוכחי (המסומן בכוכבית, למשל "*Disable,Enable" → "Disable") של הגדרה, או null</summary>
    private static string HpCurrentValue(string name)
    {
        var s = HpSettings(out string err).FirstOrDefault(x => x.name == name);
        if (err != null || s.name == null) return null;
        foreach (string part in (s.value ?? "").Split(','))
            if (part.TrimStart().StartsWith("*")) return part.Trim().TrimStart('*');
        return s.value;
    }

    /// <summary>שלב חסימה אחד ב-HP: כל ההגדרות התואמות (למשל IPv4 PXE + IPv6 PXE), כל אחת מאומתת בקריאה חוזרת</summary>
    private static (bool attempted, bool success, string settingName, string message) HpApplyStage(string stage, string currentPassword)
    {
        var spec = StageSpec(stage);
        var settings = HpSettings(out string err);
        if (err != null) return (false, false, null, "קריאת הגדרות ה-BIOS נכשלה: " + err);
        var plan = PlanStage(stage, settings);
        var parts = new List<(bool attempted, bool success, string name, string message)>();
        foreach (var p in plan)
        {
            if (p.newValue == null) { parts.Add((false, false, p.name, p.unknownMsg)); continue; }
            if (p.alreadyOk) { parts.Add((false, true, p.name, "\"" + p.name + "\" כבר \"" + p.newValue + "\"")); continue; }
            var r = HpWriteSetting(p.name, p.newValue, currentPassword);
            parts.Add((r.attempted, r.success, p.name, r.message));
        }
        return Combine(spec.NotFound, parts);
    }

    private static (bool attempted, bool success, string message) HpWriteSetting(string name, string newValue, string currentPassword)
    {
        try
        {
            // ר' הערה ב-HpSetPassword: ManagementObjectSearcher הוא הדפוס המוכח-עובד כאן, לא GetInstances()
            using var s = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT * FROM HP_BIOSSettingInterface");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    // סדר הפרמטרים האמיתי הוא (Name, Password-נוכחית, Value-חדש) - ר' הערה ב-HpSetPassword
                    uint? code = HpSetCall(o, name, HpPwdArg(currentPassword), newValue);
                    if (code != 0)
                        return (true, false, code == null
                            ? "שינוי \"" + name + "\" נכשל: ה-BIOS לא החזיר קוד תשובה"
                            : "שינוי \"" + name + "\" נכשל (קוד " + code + "): " + HpErrorText(code.Value) +
                              (code == 6 ? " — אם כבר מוגדרת סיסמת BIOS, יש להזין אותה כסיסמה נוכחית" : ""));
                    // קוד 0 לבדו לא הספיק: בעבר הקריאה נספרה כהצלחה בלי שהערך השתנה בפועל. מוודאים בקריאה חוזרת
                    string now = HpCurrentValue(name);
                    bool ok = string.Equals(now, newValue, StringComparison.OrdinalIgnoreCase);
                    return (true, ok, ok
                        ? "\"" + name + "\" שונתה ל-\"" + newValue + "\" (אומת בקריאה חוזרת). תיכנס לתוקף מההפעלה הבאה"
                        : "ה-BIOS החזיר הצלחה אבל \"" + name + "\" לא השתנתה בפועל (עכשיו: \"" + (now ?? "לא ידוע") + "\", נדרש: \"" + newValue + "\")");
                }
            }
            return (false, false, "לא נמצא ממשק HP_BIOSSettingInterface במחשב זה");
        }
        catch (Exception ex)
        {
            Config.Log("bios hp apply-setting: " + ex.Message);
            return (true, false, "שינוי \"" + name + "\" נכשל: " + ex.Message);
        }
    }
}
