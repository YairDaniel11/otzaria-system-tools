using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;

namespace OtzariaBridge;

/// <summary>
/// אכיפת מדיניות סייר הקבצים (FxPolicy/PathPolicy) וסוגי הקבצים המותרים (Kiosk.AllowedExtensions) על
/// דיאלוגי "פתיחה/שמירה" המקוריים של Windows, הנפתחים בכל תהליך במחשב — לא רק בתוך סייר הקבצים הפנימי שלנו.
///
/// הבעיה: FxPolicy/PathPolicy כבר נאכפים נכון בתוך הגשר עצמו (FsApi/RecycleApi) ובתוך תוסף "סייר קבצים"
/// הפנימי שלנו, אבל זו הגבלה על ה-UI שלנו בלבד — FxPolicy אינו ACL/minifilter ברמת מערכת ההפעלה. כל
/// תוכנה חיצונית שהמדיניות מתירה להריץ (למשל Word, ר' Programs.cs) יכולה להציג דיאלוג "פתיחת/שמירת
/// קובץ" המקורי של Windows (GetOpenFileName/IFileOpenDialog) דרך "קובץ ← פתיחה ← עיון" — דיאלוג כזה
/// מאפשר ניווט חופשי בכל מערכת הקבצים ובחירת כל סוג קובץ, ועוקף הן את FxPolicy והן את מגבלת הסיומות
/// של הקיוסק (Kiosk.AllowedExtensions, שהיום נאכפת רק ב-FsApi.Open הפנימי) לגמרי.
///
/// שיטה: SetWinEventHook גלובלי (כל התהליכים, לא רק שלנו) על EVENT_SYSTEM_DIALOGSTART ו-EVENT_OBJECT_SHOW,
/// מסונן מראש לפי class name "#32770" (הדיאלוג הסטנדרטי של Windows — גם Message-Box, גם Open/Save, גם
/// דיאלוגים אחרים). מאמתים ב-UI Automation שזה אכן דיאלוג פתיחה/שמירה מבוסס Explorer (ולא, למשל, הודעת
/// שגיאה) לפי נוכחות רכיבים ידועים (תצוגת הקבצים/סרגל הניווט). כל עוד הדיאלוג פתוח — polling קבוע קורא
/// את תיבת "שם הקובץ"/"סוג הקובץ", מקנן נתיב מלא דרכה (אם קיים) בדיוק עם PathPolicy.Resolve (אותה קנוניזציה
/// בדיוק כמו FsApi — לא נכתבה כאן שום לוגיקת קנוניזציה חדשה) ובודק מול FxPolicy.IsPathAllowed, וגם בודק את
/// סיומת שם-הקובץ המוצג מול Kiosk.DialogGuardAllowedExtensions ורשימת סיומות מסוכנות קבועה. חריגה ממדיניות
/// כלשהי -> סוגרים את הדיאלוג (WM_CLOSE) ומציגים התראה במגש. ר' "מגבלות ידועות" למטה — זו אכיפה אמינה
/// ומשמעותית, אבל לא אטומה ב-100%, ואין לשווק אותה ככזו.
///
/// למה לא תהליך עזר נפרד (--dialog-guard) כמוצע במשימה המקורית: הבעיה שפתרון כזה נועד לפתור (השירות
/// HardLockService רץ כ-SYSTEM בסשן 0 ולא יכול לגעת בדסקטופ האינטראקטיבי) כבר פתורה במבנה הקיים: הגשר
/// הרגיל (לא --service) כבר רץ באופן קבוע בכל סשן משתמש אינטראקטיבי (Installer.cs קובע Run key שמפעיל
/// אותו בכל כניסה; HardLockService.EnsureBridgeInSessions מוודא שהוא חוזר תוך שניות אם נסגר כש-HardLock
/// פעיל). יש לו כבר לולאת הודעות STA (Tray.Run -> Application.Run) בדיוק כפי ש-SetWinEventHook עם
/// WINEVENT_OUTOFCONTEXT דורש, וגישה ישירה (בתוך-תהליך) ל-FxPolicy/PathPolicy/Kiosk בלי IPC נוסף.
///
/// הערה: למידע על מגבלות והתנהגויות קצה - פנו למפתח יאיר דניאל.
/// </summary>
public static class DialogGuard
{
    private const string DialogClass = "#32770";
    private const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const uint WM_CLOSE = 0x0010;
    private const int PollMs = 400;
    private const int PollFgMs = 120;

    // סיומות "מסוכנות": חסומות תמיד (לא ניתנות להתרה דרך kiosk.allowOpenExtensions) כל עוד מגבלה
    // כלשהי פעילה (Kiosk.IsActive או FxPolicy != open) — כולל json, כדי לכסות גם קבצי הגדרות כמו config.json
    private static readonly HashSet<string> DangerousExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "bat", "cmd", "ps1", "ps1xml", "psc1", "psd1", "psm1", "vbs", "vbe", "vb", "wsc",
        "js", "jse", "wsf", "wsh", "msi", "msc", "scr", "com", "exe", "cpl", "hta", "jar",
        "reg", "lnk", "url", "scf", "appref-ms", "dll", "sys", "inf", "json", "py", "pyw",
        "sh", "gadget", "application", "msp", "mst", "pif", "chm",
        "msix", "msixbundle", "appx", "appxbundle", "iso", "vhd", "vhdx", "rdp", "jnlp", "library-ms",
        "search-ms", "settingcontent-ms", "diagcab", "ocx", "xll", "wll", "theme", "themepack",
    };

    private const int DetectWindowMs = 3000;

    private delegate bool EnumChildProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr l);

    private static IUIAutomation TryCreateUi()
    {
        try { return new CUIAutomation8(); }
        catch { }
        try { return new CUIAutomation(); }
        catch { return null; }
    }

    /// <summary>מחלקות כל חלונות הצאצא של חלון (כל העומק)</summary>
    private static List<string> ChildClasses(IntPtr hwnd)
    {
        var res = new List<string>();
        var sb = new StringBuilder(128);
        try
        {
            EnumChildWindows(hwnd, (h, _) => { sb.Clear(); if (GetClassName(h, sb, sb.Capacity) > 0) res.Add(sb.ToString()); return true; }, IntPtr.Zero);
        }
        catch { }
        return res;
    }

    // [s2] רכיבי דיאלוג קבצים של comdlg32/IFileDialog. הראשונים הם הוכחה; האחרים (סרגל כתובת/תצוגת DirectUI) מספיקים כדי לנהוג בדיאלוג כדיאלוג קבצים
    private static readonly HashSet<string> FileDialogChildClasses = new(StringComparer.Ordinal)
        { "SHELLDLL_DefView", "DUIViewWndClassName", "DirectUIHWND", "ComboBoxEx32", "ReBarWindow32", "Address Band Root", "ShellTabWindowClass" };

    /// <summary>[s2] זיהוי דיאלוג קבצים לפי מחלקות הילדים (טהור, בלי UIA). MessageBox מכיל רק Static/Button</summary>
    internal static bool LooksLikeFileDialogByChildren(IEnumerable<string> childClasses) =>
        childClasses != null && childClasses.Any(FileDialogChildClasses.Contains);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // שומרים את ה-delegate בשדה סטטי: אם ה-GC אוסף אותו, SetWinEventHook קורא לכתובת מתה -> קריסה
    private static WinEventDelegate _callback;
    private static IntPtr _hookDialogStart, _hookObjectShow;
    private static readonly ConcurrentDictionary<IntPtr, byte> _tracked = new();
    // דיביונס: אותה הפרה צריכה להופיע פעמיים ברצף (מרווח poll אחד) לפני שסוגרים בפועל (ר' מגבלה #4)
    private static readonly ConcurrentDictionary<IntPtr, string> _lastViolation = new();
    private static bool _started;
    // דיאלוגי פתיחה/שמירה שאומתו (לא Message-Box) — לחסימת תפריט הקשר (עכבר ב-Kiosk, מקלדת ב-KioskKeys)
    private static readonly ConcurrentDictionary<IntPtr, byte> _fileDialogs = new();
    private static volatile bool _restrictNow;

    /// <summary>האם חלון החזית הוא דיאלוג קבצים שמגבילים כרגע (לחסימת מקש התפריט / Shift+F10). זול: נקרא מתוך hook</summary>
    internal static bool IsFileDialogForeground() => _restrictNow && !_fileDialogs.IsEmpty && _fileDialogs.ContainsKey(Kiosk.ForegroundRoot());

    // [r1] דיאלוגים שתיבת השם שלהם מכילה כרגע ערך חורג (עד הדיביונס): Enter נחסם במקלדת (KioskKeys) כדי שלא יבצע ניווט/פתיחה לפני הסריקה
    private static readonly ConcurrentDictionary<IntPtr, byte> _unsafeEntry = new();
    private static readonly ConcurrentDictionary<IntPtr, byte> _routed = new();

    /// <summary>האם בדיאלוג הקבצים שבחזית יש כרגע ערך אסור (לחסימת Enter). זול: נקרא מתוך hook</summary>
    internal static bool IsUnsafeEntryForeground() => !_unsafeEntry.IsEmpty && _unsafeEntry.ContainsKey(Kiosk.ForegroundRoot());

    /// <summary>
    /// [r1] ערך בתיבת השם שמנווט במקום לבחור קובץ: shell:, מזהה CLSID (::{...}), UNC, כתובת (http/ftp/file), משתני סביבה (%..%),
    /// נקודות בלבד (.. / .). אלה עוקפים את בדיקת הנתיב/הסיומת כי רק אחרי Enter הם הופכים לתיקיה בפועל.
    /// </summary>
    internal static bool IsNavigationToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim().Trim('"');
        if (s.Contains("(*.", StringComparison.Ordinal)) return false;   // תבנית סוג-קובץ, לא שם
        return s.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || s.Contains("::{", StringComparison.Ordinal) ||
               s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal) ||
               s.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
               (s.Length > 2 && s[0] == '%' && s.IndexOf('%', 1) > 0) || s == "." || s == ".." || s.StartsWith(@"..\", StringComparison.Ordinal);
    }

    /// <summary>
    /// [r1] הכתובת הנוכחית של הדיאלוג אינה נתיב מלא (This PC, Network, Control Panel, ספריות...): אי אפשר להכריע במדיניות לפי נתיב,
    /// ולכן מנתבים לתיקיה מותרת או סוגרים. כתובת ריקה/לא נקראה = לא מתערבים (מגבלה #3).
    /// </summary>
    internal static bool AddressNeedsRouting(string address) => !string.IsNullOrWhiteSpace(address) && !IsFullPath(address);

    /// <summary>האם לבלוע לחיצת עכבר ימנית בנקודה: תפריט ההקשר בדיאלוג (פתיחה/מאפיינים/הפעלה כמנהל) עוקף את הבדיקות</summary>
    internal static bool ShouldSwallowMouse(int x, int y) =>
        _restrictNow && !_fileDialogs.IsEmpty && _fileDialogs.ContainsKey(Kiosk.RootWindowAt(x, y));

    public static void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _callback = OnWinEvent;
            _hookDialogStart = SetWinEventHook(EVENT_SYSTEM_DIALOGSTART, EVENT_SYSTEM_DIALOGSTART, IntPtr.Zero,
                _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            // רשת ביטחון: לא כל תוכנה שולחת DIALOGSTART באופן אמין; EVENT_OBJECT_SHOW תמיד נורה כשחלון עולה
            _hookObjectShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero,
                _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (_hookDialogStart == IntPtr.Zero && _hookObjectShow == IntPtr.Zero)
                Config.Log("dialog-guard: SetWinEventHook נכשל לגמרי — אכיפת דיאלוגי קבצים מערכתית לא פעילה");
        }
        catch (Exception ex) { Config.Log("dialog-guard: עלייה נכשלה: " + ex.Message); }
    }

    private static void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            if (eventType == EVENT_OBJECT_SHOW && (idObject != OBJID_WINDOW || idChild != CHILDID_SELF)) return;
            var sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) <= 0) return;
            if (sb.ToString() != DialogClass) return;
            if (!_tracked.TryAdd(hwnd, 0)) return; // כבר במעקב
            SafeTask.Run(() => MonitorDialog(hwnd));
        }
        catch (Exception ex) { Config.Log("dialog-guard event: " + ex.Message); }
    }

    /// <summary>רץ ברקע לכל דיאלוג #32770 חדש: מאמת שזה Open/Save ואז אוכף עד שהוא נסגר</summary>
    private static void MonitorDialog(IntPtr hwnd)
    {
        try
        {
            // [s2] WHY: בדיקה חד-פעמית אחרי 80ms נפתחה לכשל (עץ UIA עדיין ריק/COM נכשל = הדיאלוג לא נאכף לעולם). עכשיו: ניסיון חוזר עד ~3 שניות,
            // זיהוי ראשי לפי חלונות ילד מקוריים (SHELLDLL_DefView/DUIViewWndClassName) בלי UIA, ו-UIA רק כגיבוי
            IUIAutomation ui = null;
            bool isFile = false;
            long deadline = Environment.TickCount64 + DetectWindowMs;
            for (int attempt = 0; IsWindow(hwnd) && Environment.TickCount64 < deadline; attempt++)
            {
                if (LooksLikeFileDialogByChildren(ChildClasses(hwnd))) { isFile = true; break; }
                if (attempt % 4 == 3)
                {
                    ui ??= TryCreateUi();
                    if (ui != null && LooksLikeFileDialog(ui, hwnd)) { isFile = true; break; }
                }
                Thread.Sleep(60);
            }
            if (!isFile || !IsWindow(hwnd)) return; // Message-Box או דיאלוג סטנדרטי אחר (אין בו רכיבי דיאלוג קבצים) — לא נוגעים בו
            ui ??= TryCreateUi();
            if (ui == null && (Kiosk.IsActive || FxPolicy.EffectiveMode() != "open"))
            {
                // [s2] זוהה דיאלוג קבצים אבל אי אפשר לקרוא אותו (UIA לא זמין): נכשלים סגור
                Close(hwnd, Lang.T("אי אפשר לאמת את דיאלוג הקבצים בזמן נעילה.", "This file dialog can't be verified while locked."));
                return;
            }
            Config.Log("dialog-guard: זוהה דיאלוג פתיחה/שמירה (hwnd=0x" + hwnd.ToString("X") + ")");
            _fileDialogs[hwnd] = 0;

            while (IsWindow(hwnd))
            {
                string mode = FxPolicy.EffectiveMode();
                _restrictNow = Kiosk.IsActive || mode != "open";
                string violation = DetectViolation(ui, hwnd, mode, out string message);
                int poll = Kiosk.ForegroundRoot() == hwnd ? PollFgMs : PollMs;   // [r1] בחזית: סריקה מהירה (חלון ה-race לפני Enter)
                if (violation == null) { _lastViolation.TryRemove(hwnd, out _); _unsafeEntry.TryRemove(hwnd, out _); Thread.Sleep(poll); continue; }

                _unsafeEntry[hwnd] = 0;   // [r1] מכאן ועד שהערך תוקן: Enter במקלדת נחסם (KioskKeys)

                // [r1] כתובת שאינה נתיב מלא: ניתוב חד-פעמי לתיקיה מותרת; אם נכשל/חזר — סגירה
                if (violation.StartsWith("addr:", StringComparison.Ordinal))
                {
                    if (_routed.TryAdd(hwnd, 0) && TryRouteToSafeFolder(ui, hwnd)) { Thread.Sleep(poll); continue; }
                    Close(hwnd, message);
                    return;
                }

                // "נעול", נתיב אסור וסיומת מסוכנת אינם תלויים בהקלדה באמצע (אין רישא תמימה של סיומת מסוכנת) — בלי דיביונס.
                // רק "סיומת שאינה ברשימה" מקבלת דיביונס, כי "report.d" באמצע הקלדת "report.docx" הוא לגיטימי
                if (IsImmediate(violation) || _lastViolation.TryGetValue(hwnd, out var prev) && prev == violation)
                {
                    Close(hwnd, message);
                    return;
                }
                _lastViolation[hwnd] = violation;
                Thread.Sleep(poll);
            }
        }
        catch (Exception ex) { Config.Log("dialog-guard monitor: " + ex.Message); }
        finally { _tracked.TryRemove(hwnd, out _); _lastViolation.TryRemove(hwnd, out _); _fileDialogs.TryRemove(hwnd, out _); _unsafeEntry.TryRemove(hwnd, out _); _routed.TryRemove(hwnd, out _); }
    }

    internal static bool IsImmediate(string violation) =>
        violation == "locked" || violation.StartsWith("path:", StringComparison.Ordinal) || violation.StartsWith("danger:", StringComparison.Ordinal) ||
        violation.StartsWith("nav:", StringComparison.Ordinal);

    /// <summary>
    /// [r1] ניתוב דיאלוג שנמצא בכתובת שאינה נתיב מלא: כותבים לתיבת השם תיקיה מותרת (Documents/Desktop לפי המדיניות) ולוחצים על הכפתור
    /// הראשי (AutomationId "1") — בתיבה עם נתיב תיקיה הוא מנווט ולא פותח/שומר. לא נבדק מול דיאלוג חי (ר' הדוח); כשל = סגירה.
    /// </summary>
    private static bool TryRouteToSafeFolder(IUIAutomation ui, IntPtr hwnd)
    {
        try
        {
            string safe = null;
            foreach (var sf in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory })
            {
                string d = Environment.GetFolderPath(sf);
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                bool ok = FxPolicy.EffectiveMode() == "restricted" ? FxPolicy.IsPathAllowed(d) : !KioskBaselineDenied(d);
                if (ok) { safe = d; break; }
            }
            if (safe == null) return false;
            var root = ui.ElementFromHandle(hwnd);
            var combo = root?.FindFirst(TreeScope.TreeScope_Descendants, ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1148"));
            var edit = combo?.FindFirst(TreeScope.TreeScope_Descendants,
                ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_ControlTypePropertyId, (int)UIA_ControlTypeIds.UIA_EditControlTypeId)) ?? combo;
            if (edit?.GetCurrentPattern((int)UIA_PatternIds.UIA_ValuePatternId) is not IUIAutomationValuePattern vp) return false;
            vp.SetValue(safe);
            var btn = root.FindFirst(TreeScope.TreeScope_Descendants, ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1"));
            if (btn?.GetCurrentPattern((int)UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern ip) return false;
            ip.Invoke();
            Config.Log("dialog-guard: דיאלוג בכתובת שאינה נתיב מלא נותב לתיקיה מותרת (hwnd=0x" + hwnd.ToString("X") + ")");
            return true;
        }
        catch (Exception ex) { Config.Log("dialog-guard route: " + ex.Message); return false; }
    }

    /// <summary>null = אין הפרה כרגע. אחרת מחזיר "מפתח הפרה" יציב (לדיביונס) ומציב את הודעת הטוסט ב-message</summary>
    private static string DetectViolation(IUIAutomation ui, IntPtr hwnd, string mode, out string message)
    {
        message = null;
        bool restrictingNow = Kiosk.IsActive || mode != "open";
        List<string> values = ReadCandidateValues(ui, hwnd);

        var allowedExts = restrictingNow ? Kiosk.DialogGuardAllowedExtensions() : null;
        if (restrictingNow)
        {
            var allowed = allowedExts;
            foreach (string raw in values)
            {
                // [r1] shell:/CLSID/UNC/כתובת/משתנה סביבה בתיבת השם: ניווט שעוקף את בדיקת הנתיב — נחסם לפני ש-Enter ירוץ
                if (IsNavigationToken(raw))
                {
                    message = Lang.T("אי אפשר להקליד כתובת מיוחדת (shell:, רשת וכד') בדיאלוג בזמן נעילה.",
                                      "Special addresses (shell:, network, etc.) can't be typed into this dialog while locked.");
                    return "nav:" + raw.Trim().ToLowerInvariant();
                }
                foreach (string ext in ExtensionsOf(raw))
                {
                    if (DangerousExt.Contains(ext))
                    {
                        message = Lang.T($"סוג קובץ זה (.{ext}) חסום לבחירה מטעמי אבטחה.",
                                          $"This file type (.{ext}) can't be selected for security reasons.");
                        return "danger:" + ext;
                    }
                    if (!allowed.Contains(ext))
                    {
                        message = Lang.T($"סוג קובץ זה (.{ext}) אינו ברשימת סוגי הקבצים המותרים כרגע.",
                                          $"This file type (.{ext}) isn't in the currently allowed list.");
                        return "ext:" + ext;
                    }
                }
            }
        }

        if (restrictingNow)
        {
            // [s2] קובץ (לא תיקיה) בלי סיומת: Windows/Word פותחים אותו לפי תוכן, ובדיקת הסיומת לא ראתה אותו. נבדקת רק תיבת "שם הקובץ" (AutomationId 1148)
            // [u2] WHY: בעבר "Doc1" ב-Save As של Word (או שם בלי סיומת ב-Open כשהסיומות מוסתרות) סומן הפרה ו-Enter נחסם. עכשיו: Open - רק אם הקובץ
            // הקיים בדיסק באמת בלי סיומת / עם סיומת אסורה; Save - Windows משלימה סיומת מסוג הקובץ שנבחר, ולכן נדרש סוג קובץ מותר ידוע
            string fname = ReadFileNameValue(ui, hwnd);
            bool isSave = IsSaveDialog(hwnd);
            if (fname != null && ExtensionlessViolation(fname, ReadCurrentFolder(ui, hwnd), isSave, isSave ? ReadSaveTypeExt(ui, hwnd) : null,
                                                        allowedExts, DangerousExt, Directory.Exists, FindExistingByStem))
            {
                message = Lang.T("אי אפשר לבחור קובץ בלי סיומת בזמן נעילה.", "Files without an extension can't be selected while locked.");
                return "noext:" + fname.Trim().ToLowerInvariant();
            }
        }

        if (mode == "locked")
        {
            message = Lang.T("סייר הקבצים נעול — אי אפשר להשתמש כרגע בדיאלוגי פתיחה/שמירה של קבצים.",
                              "The file browser is locked — file open/save dialogs are blocked right now.");
            return "locked";
        }

        // נתיבים: כל נתיב מלא בתיבות הטקסט + התיקייה הנוכחית (אם נקראה מסרגל הכתובת). בלי דיביונס כשהתיקייה אסורה
        var paths = values.Where(IsFullPath).Select(v => v.Trim().Trim('"')).ToList();
        string cur = ReadCurrentFolder(ui, hwnd);
        if (cur != null) paths.Add(cur);
        else if (restrictingNow)
        {
            // [r1] הכתובת הנוכחית אינה נתיב מלא (This PC / Network / לוח הבקרה / shell:...) — ניתוב או סגירה (ר' TryRouteToSafeFolder)
            string addr = ReadAddressText(ui, hwnd);
            if (AddressNeedsRouting(addr))
            {
                message = Lang.T("הדיאלוג מוצג בתיקיית מערכת/רשת שאי אפשר לאמת. נעבור לתיקיה מותרת.",
                                  "The dialog is showing a system/network location that can't be verified.");
                return "addr:" + addr;
            }
        }

        if (mode == "restricted")
        {
            foreach (string path in paths)
                if (!FxPolicy.IsPathAllowed(path))
                {
                    message = Lang.T("הגישה לתיקייה הזו חסומה במדיניות סייר הקבצים.",
                                      "Access to this folder is blocked by the file-browser policy.");
                    return "path:" + path.ToLowerInvariant();
                }
        }
        else if (Kiosk.IsActive && mode == "open" && Kiosk.TempUnlockSecondsLeft() == 0)
        {
            // FxPolicy=open לא מתיר הכול כשהקיוסק פעיל: לפחות תיקיות מערכת/תוכנות/הגדרות הגשר ונתיבי רשת חסומים
            foreach (string path in paths)
                if (KioskBaselineDenied(path))
                {
                    message = Lang.T("התיקייה הזו חסומה בזמן נעילה.", "This folder is blocked while the computer is locked.");
                    return "path:" + path.ToLowerInvariant();
                }
        }

        return null;
    }

    /// <summary>
    /// [u2] האם שם בתיבת "שם הקובץ" הוא הפרה של כלל "קובץ בלי סיומת". תיקיה קיימת (ניווט) ותבניות (* ?) מותרות.
    /// Open: הפרה רק אם בדיסק קיים קובץ בדיוק בשם הזה בלי סיומת, או שקיים "שם.*" עם סיומת מסוכנת/אסורה (סיומות מוסתרות: "report" = report.docx).
    ///       שם שלא קיים בדיסק אינו נפתח ולכן אינו הפרה. תיקיה נוכחית לא ידועה ושם יחסי = הפרה (fail-closed, עם דיביונס).
    /// Save: Windows משלימה סיומת מסוג הקובץ ("Word Document (*.docx)"), לכן שם בלי סיומת תקין כשסוג הקובץ הנבחר (saveTypeExt) הוא סיומת מותרת ולא מסוכנת;
    ///       סוג לא ידוע/"כל הקבצים" = הפרה (fail-closed).
    /// </summary>
    internal static bool ExtensionlessViolation(string raw, string curFolder, bool isSave, string saveTypeExt, ISet<string> allowed, ISet<string> dangerous,
                                                Func<string, bool> dirExists, Func<string, string, IEnumerable<string>> findExistingByStem)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();
        if (s.Contains("(*.", StringComparison.Ordinal)) return false;
        var names = new List<string>();
        if (s.Contains('"'))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, "\"([^\"]*)\"")) names.Add(m.Groups[1].Value);
        else names.Add(s);
        bool IsOk(string e) => !string.IsNullOrEmpty(e) && !dangerous.Contains(e) && (allowed == null || allowed.Contains(e));
        foreach (string n in names)
        {
            string t = NormalizeName(n.Trim().Trim('"'));
            if (t.Length == 0 || t.IndexOfAny(new[] { '*', '?' }) >= 0) continue;
            if (ExtOfName(n) != null) continue;   // סיומת מפורשת נבדקת ב-ExtensionsOf (ext:/danger:)
            string full = null;
            try { full = IsFullPath(t) ? t : (curFolder != null ? Path.Combine(curFolder.Trim(), t) : null); } catch { }
            if (full != null && dirExists(full)) continue;
            if (isSave)
            {
                if (!IsOk(saveTypeExt)) return true;
                continue;
            }
            if (full == null) return true;   // Open: שם יחסי בלי תיקיה ידועה - לא ניתן לאמת
            string dir = Path.GetDirectoryName(full), stem = Path.GetFileName(full);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return true;
            foreach (string existing in findExistingByStem(dir, stem) ?? Array.Empty<string>())
            {
                string e = ExtOfName(Path.GetFileName(existing));
                if (e == null || !IsOk(e)) return true;
            }
        }
        return false;
    }

    /// <summary>קבצים בתיקיה ששמם בדיוק stem (בלי סיומת) או stem.* . כשל בגישה = ריק</summary>
    private static IEnumerable<string> FindExistingByStem(string dir, string stem)
    {
        try
        {
            return Directory.EnumerateFiles(dir, stem + "*").Where(f =>
            {
                string n = Path.GetFileName(f);
                return n.Equals(stem, StringComparison.OrdinalIgnoreCase) || n.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
            }).Take(50).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>[u2] דיאלוג שמירה: כותרת/כפתור ראשי מכילים "Save"/"שמור"/"שמירה" (לא נבדק מול דיאלוג חי)</summary>
    internal static bool LooksLikeSaveTitle(string title, string okText)
    {
        static bool Has(string t) => !string.IsNullOrEmpty(t) &&
            (t.Contains("save", StringComparison.OrdinalIgnoreCase) || t.Contains("שמור", StringComparison.Ordinal) || t.Contains("שמירה", StringComparison.Ordinal));
        return Has(title) || Has(okText);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr h, int id);

    private static bool IsSaveDialog(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256); GetWindowTextW(hwnd, sb, 256);
            var ok = new StringBuilder(64); IntPtr b = GetDlgItem(hwnd, 1); if (b != IntPtr.Zero) GetWindowTextW(b, ok, 64);
            return LooksLikeSaveTitle(sb.ToString(), ok.ToString());
        }
        catch { return false; }
    }

    /// <summary>[u2] הסיומת הראשונה מתוך טקסט סוג הקובץ ("Word Document (*.docx)" / "*.txt;*.log"), או null</summary>
    internal static string FirstExtOfFilterText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\*\.([A-Za-z0-9_\-]+)");
        return m.Success && m.Groups[1].Value != "*" ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>[u2] סוג הקובץ הנבחר בדיאלוג שמירה (combo AutomationId 1136), או null אם לא נקרא. לא נבדק מול דיאלוג חי</summary>
    private static string ReadSaveTypeExt(IUIAutomation ui, IntPtr hwnd)
    {
        try
        {
            var root = ui.ElementFromHandle(hwnd);
            var combo = root?.FindFirst(TreeScope.TreeScope_Descendants, ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1136"));
            if (combo == null) return null;
            string v = GetElementValue(combo);
            if (string.IsNullOrEmpty(v)) { try { v = combo.CurrentName; } catch { } }
            return FirstExtOfFilterText(v);
        }
        catch { return null; }
    }

    /// <summary>[s2] הערך בתיבת "שם הקובץ" (AutomationId 1148) בלבד, או null אם לא נמצאה. לא נבדק מול דיאלוג חי</summary>
    private static string ReadFileNameValue(IUIAutomation ui, IntPtr hwnd)
    {
        try
        {
            var root = ui.ElementFromHandle(hwnd);
            var combo = root?.FindFirst(TreeScope.TreeScope_Descendants, ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1148"));
            if (combo == null) return null;
            var edit = combo.FindFirst(TreeScope.TreeScope_Descendants,
                ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_ControlTypePropertyId, (int)UIA_ControlTypeIds.UIA_EditControlTypeId)) ?? combo;
            return GetElementValue(edit);
        }
        catch { return null; }
    }

    /// <summary>חסימת בסיס בנעילה כש-FxPolicy=open: Windows, Program Files, ProgramData, תיקיית הגשר, Startup ושיתופי רשת. כשל בפענוח = חסום</summary>
    internal static bool KioskBaselineDenied(string path)
    {
        string canon;
        try { canon = PathPolicy.Resolve(path); }
        catch { return true; }
        if (canon.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            Config.Dir,
        };
        foreach (string r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            try { if (PathPolicy.IsUnder(canon, PathPolicy.Resolve(r))) return true; } catch { }
        }
        return false;
    }

    /// <summary>
    /// התיקייה הנוכחית של הדיאלוג מסרגל הכתובת (AutomationId 1001, שם בצורת "Address: C:\path"). רק נתיב מלא ותקין —
    /// לא מנחשים מ-breadcrumb ידידותי ("This PC > ..."): טעות שם היא התרה שגויה. ר' מגבלה #3. לא נבדק מול דיאלוג חי
    /// </summary>
    private static string ReadCurrentFolder(IUIAutomation ui, IntPtr hwnd)
    {
        string rest = ReadAddressText(ui, hwnd);
        return rest != null && IsFullPath(rest) ? rest : null;
    }

    /// <summary>[r1] טקסט הכתובת הגולמי מסרגל הכתובת (אחרי "Address: "), או null אם לא נקרא. לא נבדק מול דיאלוג חי</summary>
    private static string ReadAddressText(IUIAutomation ui, IntPtr hwnd)
    {
        try
        {
            var root = ui.ElementFromHandle(hwnd);
            var el = root?.FindFirst(TreeScope.TreeScope_Descendants,
                ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1001"));
            string name = el?.CurrentName;
            if (string.IsNullOrWhiteSpace(name)) return null;
            int i = name.IndexOf(": ", StringComparison.Ordinal);
            string rest = (i >= 0 ? name[(i + 2)..] : name).Trim();
            return rest.Length == 0 ? null : rest;
        }
        catch { return null; }
    }

    internal static bool IsFullPath(string v)
    {
        try { return !string.IsNullOrWhiteSpace(v) && Path.IsPathFullyQualified(v.Trim()); }
        catch { return false; }
    }

    /// <summary>
    /// נרמול שם קובץ כפי ש-Windows פותחת אותו: נקודות ורווחים בסוף נמחקים ("evil.exe." = "evil.exe"), וזרם נתונים
    /// חלופי ("evil.exe:x", "evil.exe::$DATA") נחתך. בלי זה "evil.exe." עקף את בדיקת הסיומת.
    /// </summary>
    internal static string NormalizeName(string s)
    {
        s = s.Trim().TrimEnd('.', ' ');
        int from = (s.Length >= 2 && s[1] == ':') ? 2 : 0;   // נקודתיים של אות הכונן אינן זרם
        int colon = s.IndexOf(':', from);
        if (colon > 0) s = s[..colon];
        return s.TrimEnd('.', ' ');
    }

    private static string ExtOfName(string name)
    {
        try
        {
            string n = NormalizeName(name.Trim().Trim('"'));
            string ext = Path.GetExtension(n).TrimStart('.').TrimEnd('.', ' ');
            return string.IsNullOrEmpty(ext) ? null : ext;
        }
        catch { return null; }
    }

    /// <summary>
    /// כל הסיומות מתוך ערך גולמי של תיבת "שם הקובץ": נתיב מלא, שם בלבד, או בחירה מרובה ("a" "b"). בלי מרכאות ועם רווחים —
    /// הסיומת של המחרוזת כולה (מה ש-Windows תפתח) + כל מילה שסיומתה מסוכנת (כדי ש"v1.2 notes.docx" לא ייחסם סתם)
    /// </summary>
    internal static List<string> ExtensionsOf(string raw)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return res;
        string s = raw.Trim();
        // תבנית סינון סוג-קובץ ("Word Document (*.docx)") — לא שם קובץ, מתעלמים (ר' ReadCandidateValues)
        if (s.Contains("(*.", StringComparison.Ordinal)) return res;
        if (s.Contains('"'))
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, "\"([^\"]*)\""))
            { string e = ExtOfName(m.Groups[1].Value); if (e != null) res.Add(e); }
            return res;
        }
        string whole = ExtOfName(s);
        if (whole != null) res.Add(whole);
        if (!IsFullPath(s) && s.Contains(' '))
            foreach (string tok in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string e = ExtOfName(tok);
                if (e != null && DangerousExt.Contains(e) && !res.Contains(e)) res.Add(e);
            }
        return res;
    }

    /// <summary>
    /// זיהוי "זה דיאלוג פתיחה/שמירה מבוסס Explorer" לפי AutomationId יציב לאורך גרסאות Windows:
    /// "1000" (תצוגת רשימת הקבצים/התיקיות המשותפת) או "1001" (סרגל הניווט/breadcrumb). לא נבדק
    /// אינטראקטיבית מול דיאלוג חי בסביבת הפיתוח הזו — ר' מגבלה #2 בתיעוד המחלקה.
    /// </summary>
    internal static bool LooksLikeFileDialog(IUIAutomation ui, IntPtr hwnd)
    {
        try
        {
            IUIAutomationElement root = ui.ElementFromHandle(hwnd);
            if (root == null) return false;
            var c1 = ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1000");
            var c2 = ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_AutomationIdPropertyId, "1001");
            var or = ui.CreateOrCondition(c1, c2);
            return root.FindFirst(TreeScope.TreeScope_Descendants, or) != null;
        }
        catch (Exception ex) { Config.Log("dialog-guard detect: " + ex.Message); return false; }
    }

    /// <summary>
    /// כל הערכים הטקסטואליים (לא ריקים) מרכיבי Edit/ComboBox בדיאלוג — כולל גם תיבת "סוג הקובץ" (לכן
    /// ExtensionOf מסנן ערכים בתבנית "(*.ext)" בנפרד, ר' שם) וגם תיבת "שם הקובץ" עצמה.
    /// </summary>
    private static List<string> ReadCandidateValues(IUIAutomation ui, IntPtr hwnd)
    {
        var result = new List<string>();
        try
        {
            IUIAutomationElement root = ui.ElementFromHandle(hwnd);
            if (root == null) return result;
            var condEdit = ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_ControlTypePropertyId, (int)UIA_ControlTypeIds.UIA_EditControlTypeId);
            var condCombo = ui.CreatePropertyCondition((int)UIA_PropertyIds.UIA_ControlTypePropertyId, (int)UIA_ControlTypeIds.UIA_ComboBoxControlTypeId);
            var or = ui.CreateOrCondition(condEdit, condCombo);
            IUIAutomationElementArray arr = root.FindAll(TreeScope.TreeScope_Descendants, or);
            for (int i = 0; i < (arr?.Length ?? 0); i++)
            {
                string val = GetElementValue(arr.GetElement(i));
                if (!string.IsNullOrWhiteSpace(val)) result.Add(val);
            }
        }
        catch (Exception ex) { Config.Log("dialog-guard read-values: " + ex.Message); }
        return result;
    }

    private static string GetElementValue(IUIAutomationElement el)
    {
        try
        {
            if (el?.GetCurrentPattern((int)UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern vp)
            {
                string v = vp.CurrentValue;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }
        try
        {
            if (el?.GetCurrentPattern((int)UIA_PatternIds.UIA_LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern lp)
            {
                string v = lp.CurrentValue;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }
        return null;
    }

    private static void Close(IntPtr hwnd, string message)
    {
        try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
        Config.Log("dialog-guard: נחסם דיאלוג קבצים חורג ממדיניות (hwnd=0x" + hwnd.ToString("X") + "): " + message);
        Tray.Notify(Lang.T("גישה לקבצים נחסמה", "File access blocked"), message);
    }
}
