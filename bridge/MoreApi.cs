using System.Drawing;
using System.Drawing.Printing;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace OtzariaBridge;

/// <summary>הגדרות 1.8.0: עכבר ומשטח מגע, מצב טיסה, מדפסות, אזור זמן ושעה, ושפת ברירת מחדל למקלדת.
/// מה שלא נתמך במחשב או דורש הרשאות מנהל מחזיר הודעה ברורה</summary>
public static class MoreApi
{
    public static void Register()
    {
        Server.Route("/sys/mouse/get", _ => Mouse.Get());
        Server.Route("/sys/mouse/set", Mouse.Set);
        Server.Route("/sys/airplane/get", async _ => await Airplane.Get());
        Server.Route("/sys/airplane/set", Airplane.Set);
        Server.Route("/sys/printers/list", _ => Printers.List());
        Server.Route("/sys/printers/set-default", b => Printers.SetDefault(b.Req("name")));
        Server.Route("/sys/printers/test-page", b => Printers.TestPage(b.Req("name")));
        Server.Route("/sys/time/get", _ => TimeSettings.Get());
        Server.Route("/sys/time/set", TimeSettings.Set);
        Server.Route("/sys/time/sync", _ => TimeSettings.Sync());
        Server.Route("/sys/keyboard/default-language/get", _ => InputDefault.Get());
        Server.Route("/sys/keyboard/default-language/set", b => InputDefault.Set(b.Str("id", "")));
    }
}

// ---------- עכבר ומשטח מגע ----------

public static class Mouse
{
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool SpiInt(uint a, uint p, ref int v, uint ini);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool SpiArr(uint a, uint p, int[] v, uint ini);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool SpiSet(uint a, uint p, IntPtr v, uint ini);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool SwapMouseButton(bool swap);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    private const uint GetSpeed = 0x70, SetSpeed = 0x71, GetMouse = 0x03, SetMouse = 0x04;
    private const uint GetWheel = 0x68, SetWheel = 0x69, SetDblClk = 0x20, SetSwap = 0x21;
    private const uint Persist = 0x01 | 0x02;
    private const string PtpKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";

    public static object Get()
    {
        int speed = 10, wheel = 3;
        SpiInt(GetSpeed, 0, ref speed, 0);
        SpiInt(GetWheel, 0, ref wheel, 0);
        var acc = new int[3];
        SpiArr(GetMouse, 0, acc, 0);
        return new
        {
            speed,                                   // 1-20
            leftHanded = GetSystemMetrics(23) != 0,  // SM_SWAPBUTTON
            doubleClickMs = (int)GetDoubleClickTime(),
            wheelLines = wheel,
            precision = acc[2] != 0,                 // "שיפור דיוק המצביע"
            touchpad = Touchpad(),
        };
    }

    private static object Touchpad()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PtpKey);
            if (k == null || k.GetValue("AAPThreshold") == null && k.GetValue("TapsEnabled") == null) return null;
            int Dw(string n, int f) { try { return Convert.ToInt32(k.GetValue(n, f)); } catch { return f; } }
            return new
            {
                taps = Dw("TapsEnabled", -1) != 0,
                twoFingerRightClick = Dw("TwoFingerTapEnabled", -1) != 0,
                naturalScroll = Dw("ScrollDirection", 0) != 0,
                sensitivity = Dw("AAPThreshold", 2),   // 0 = הכי רגיש ... 3 = נמוך
                cursorSpeed = Dw("CursorSpeed", 10),   // 0-20
            };
        }
        catch { return null; }
    }

    public static object Set(JsonElement b)
    {
        double? sp = b.Num("speed");
        if (sp.HasValue) SpiSet(SetSpeed, 0, new IntPtr((int)Math.Clamp(Math.Round(sp.Value), 1, 20)), Persist);

        bool? left = b.BoolOpt("leftHanded");
        if (left.HasValue)
        {
            SpiSet(SetSwap, left.Value ? 1u : 0u, IntPtr.Zero, Persist);
            SwapMouseButton(left.Value);
        }
        double? dbl = b.Num("doubleClickMs");
        if (dbl.HasValue) SpiSet(SetDblClk, (uint)Math.Clamp(Math.Round(dbl.Value), 200, 900), IntPtr.Zero, Persist);
        double? wh = b.Num("wheelLines");
        if (wh.HasValue) SpiSet(SetWheel, (uint)Math.Clamp(Math.Round(wh.Value), 1, 20), IntPtr.Zero, Persist);
        bool? prec = b.BoolOpt("precision");
        if (prec.HasValue)
        {
            var acc = prec.Value ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 };
            SpiArr(SetMouse, 0, acc, Persist);
        }

        // משטח מגע (Precision Touchpad): נשמר ברישום של המשתמש. Windows קורא את השינוי בכניסה הבאה למערכת
        if (b.TryGetProperty("touchpad", out var tp) && tp.ValueKind == JsonValueKind.Object)
        {
            using var k = Registry.CurrentUser.CreateSubKey(PtpKey);
            void Flag(string prop, string reg)
            {
                bool? v = tp.BoolOpt(prop);
                if (v.HasValue) k.SetValue(reg, v.Value ? unchecked((int)0xFFFFFFFF) : 0, RegistryValueKind.DWord);
            }
            Flag("taps", "TapsEnabled");
            Flag("twoFingerRightClick", "TwoFingerTapEnabled");
            Flag("naturalScroll", "ScrollDirection");
            double? sens = tp.Num("sensitivity");
            if (sens.HasValue) k.SetValue("AAPThreshold", (int)Math.Clamp(Math.Round(sens.Value), 0, 3), RegistryValueKind.DWord);
            double? cs = tp.Num("cursorSpeed");
            if (cs.HasValue) k.SetValue("CursorSpeed", (int)Math.Clamp(Math.Round(cs.Value), 0, 20), RegistryValueKind.DWord);
        }
        return Get();
    }
}

// ---------- מצב טיסה ----------

/// <summary>אין ב-Windows API ציבורי ל"מצב טיסה" עצמו. כאן מצב טיסה = כיבוי כל המשדרים (Wi-Fi, בלוטות', סלולר),
/// ובכיבוי מצב הטיסה המשדרים חוזרים למצב שבו היו לפני ההפעלה</summary>
public static class Airplane
{
    private static async Task<IReadOnlyList<Radio>> Radios()
    {
        var access = await Radio.RequestAccessAsync();
        if (access != RadioAccessStatus.Allowed) throw BridgeException.Forbidden("Windows לא מאפשר שליטה במשדרים (בדקו בהגדרות הפרטיות)");
        return await Radio.GetRadiosAsync();
    }

    public static async Task<object> Get()
    {
        var radios = await Radios();
        bool any = radios.Count > 0;
        bool allOff = any && radios.All(r => r.State != RadioState.On);
        return new
        {
            supported = any,
            on = allOff && Config.Get("airplane.on", false),
            radios = radios.Select(r => new { kind = r.Kind.ToString().ToLowerInvariant(), name = r.Name, on = r.State == RadioState.On }).ToList(),
        };
    }

    public static async Task<object> Set(JsonElement b)
    {
        bool on = b.Bool("on");
        var radios = await Radios();
        if (radios.Count == 0) throw BridgeException.Unsupported("לא נמצאו משדרים במחשב זה");
        if (on)
        {
            // שומרים אילו משדרים היו דולקים, כדי להחזיר בדיוק אותם
            Config.Set("airplane.restore", new JsonArray(radios.Where(r => r.State == RadioState.On)
                .Select(r => (JsonNode)r.Kind.ToString()).ToArray()));
            foreach (var r in radios) if (r.State == RadioState.On) await r.SetStateAsync(RadioState.Off);
        }
        else
        {
            var restore = Config.HasList("airplane.restore") ? Config.GetList("airplane.restore") : radios.Select(r => r.Kind.ToString()).ToArray();
            if (restore.Length == 0) restore = radios.Select(r => r.Kind.ToString()).ToArray();
            foreach (var r in radios)
                if (restore.Contains(r.Kind.ToString(), StringComparer.OrdinalIgnoreCase)) await r.SetStateAsync(RadioState.On);
        }
        Config.Set("airplane.on", on);
        Config.Save();
        return await Get();
    }
}

// ---------- מדפסות ----------

public static class Printers
{
    public static object List()
    {
        var res = new List<object>();
        using var s = new ManagementObjectSearcher("SELECT Name, Default, WorkOffline, Network, PrinterStatus, DriverName, PortName FROM Win32_Printer");
        // בעיה #14 סעיף 5: גם האוסף שמוחזר מ-Get וגם כל ManagementObject בתוכו הם IDisposable — דליפה אם לא משחררים
        using var results = s.Get();
        foreach (ManagementObject o in results)
        {
            using (o)
            {
                res.Add(new
                {
                    name = o["Name"] as string ?? "",
                    isDefault = o["Default"] is bool d && d,
                    offline = o["WorkOffline"] is bool w && w,
                    network = o["Network"] is bool n && n,
                    driver = o["DriverName"] as string ?? "",
                    port = o["PortName"] as string ?? "",
                    status = Convert.ToInt32(o["PrinterStatus"] ?? 2), // 3 = מוכנה, 4 = מדפיסה, 7 = לא מחוברת
                });
            }
        }
        bool managed = false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Windows");
            managed = Convert.ToInt32(k?.GetValue("LegacyDefaultPrinterMode", 0) ?? 0) == 0;
        }
        catch { }
        return new { printers = res, windowsManagesDefault = managed };
    }

    private static ManagementObject Find(string name)
    {
        using var s = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
        using var results = s.Get();
        foreach (ManagementObject o in results)
        {
            if (string.Equals(o["Name"] as string, name, StringComparison.OrdinalIgnoreCase)) return o;
            o.Dispose(); // לא זה שחיפשנו — משחררים מיד ולא ממתינים ל-GC
        }
        throw BridgeException.NotFound("המדפסת לא נמצאה: " + name);
    }

    public static object SetDefault(string name)
    {
        // "Windows ינהל את מדפסת ברירת המחדל" דורס בחירה ידנית, ולכן מכבים אותו כשהמשתמש בוחר מדפסת
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Windows");
            k.SetValue("LegacyDefaultPrinterMode", 1, RegistryValueKind.DWord);
        }
        catch { }
        using var p = Find(name);
        var r = p.InvokeMethod("SetDefaultPrinter", null);
        if (Convert.ToInt32(r ?? 0) != 0) throw new BridgeException("failed", "Windows לא הצליח לקבוע את המדפסת (קוד " + r + ")", 500);
        return List();
    }

    /// <summary>
    /// דף ניסיון דרך Win32_Printer.PrintTestPage() (WMI) כושל כמעט תמיד עם קוד 5 ("Access Denied") -
    /// זו תקלה מתועדת של ה-method הזה מול ספולר ה-הדפסה המודרני, במיוחד למדפסות וירטואליות/PDF (אין
    /// להן "דף ניסיון" אמיתי של דרייבר). מדפיסים בפועל דרך PrintDocument, שעובד גם למדפסות וירטואליות
    /// (יפתח/ישמור קובץ, לפי האופן שבו אותה מדפסת וירטואלית מוגדרת) ועובר דרך ספולר המשתמש הנוכחי.
    /// </summary>
    public static object TestPage(string name)
    {
        using (Find(name)) { } // זורק NotFound אם המדפסת לא קיימת
        using var pd = new PrintDocument { DocumentName = "דף ניסיון - תוכנת חסימת אוצריא", PrinterSettings = { PrinterName = name } };
        if (!pd.PrinterSettings.IsValid) throw BridgeException.NotFound("המדפסת לא נמצאה: " + name);
        pd.PrintPage += (_, e) =>
        {
            using var title = new Font("Segoe UI", 28, FontStyle.Bold);
            using var body = new Font("Segoe UI", 14);
            var x = e.MarginBounds.Left; var y = e.MarginBounds.Top;
            e.Graphics.DrawString("דף ניסיון", title, Brushes.Black, x, y); y += 50;
            e.Graphics.DrawString("מדפסת: " + name, body, Brushes.Black, x, y); y += 26;
            e.Graphics.DrawString("נשלח מתוך תוכנת חסימת אוצריא ב-" + DateTime.Now.ToString("dd/MM/yyyy HH:mm"), body, Brushes.Black, x, y);
        };
        try { pd.Print(); }
        catch (Exception ex) { throw new BridgeException("failed", "שליחת דף הניסיון נכשלה: " + ex.Message, 500); }
        return new { ok = true };
    }
}

// ---------- אזור זמן ושעה ----------

public static class TimeSettings
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Ms; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetLocalTime(ref SystemTime t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr res);

    private const string IntlKey = @"Control Panel\International";

    private static string ShortTime()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(IntlKey); return k?.GetValue("sShortTime") as string ?? ""; }
        catch { return ""; }
    }

    private static bool AutoSync()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters");
            return !string.Equals(k?.GetValue("Type") as string, "NoSync", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    public static object Get()
    {
        TimeZoneInfo.ClearCachedData();
        var tz = TimeZoneInfo.Local;
        var now = DateTime.Now;
        string st = ShortTime();
        return new
        {
            now = now.ToString("yyyy-MM-ddTHH:mm:ss"),
            zone = tz.Id,
            zoneName = tz.DisplayName,
            dst = tz.IsDaylightSavingTime(now),
            clock24 = st.Contains('H'),
            autoSync = AutoSync(),
            zones = TimeZoneInfo.GetSystemTimeZones().Select(z => new { id = z.Id, name = z.DisplayName }).ToList(),
        };
    }

    public static object Set(JsonElement b)
    {
        string zone = b.Str("zone");
        if (!string.IsNullOrEmpty(zone))
        {
            if (!TimeZoneInfo.GetSystemTimeZones().Any(z => z.Id == zone)) throw BridgeException.NotFound("אזור הזמן לא נמצא");
            // tzutil זמין לכל משתמש (הרשאת SeTimeZonePrivilege ניתנת למשתמשים רגילים)
            Config.Run("tzutil", "/s \"" + zone.Replace("\"", "") + "\"");
            TimeZoneInfo.ClearCachedData();
            if (TimeZoneInfo.Local.Id != zone) throw new BridgeException("failed", "Windows לא החליף את אזור הזמן", 500);
        }

        bool? c24 = b.BoolOpt("clock24");
        if (c24.HasValue)
        {
            using var k = Registry.CurrentUser.CreateSubKey(IntlKey);
            k.SetValue("sShortTime", c24.Value ? "HH:mm" : "h:mm tt");
            k.SetValue("sTimeFormat", c24.Value ? "HH:mm:ss" : "h:mm:ss tt");
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "intl", 2, 3000, out _);
        }

        string at = b.Str("dateTime"); // "yyyy-MM-ddTHH:mm"
        if (!string.IsNullOrEmpty(at))
        {
            if (!DateTime.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t))
                throw BridgeException.BadRequest("תאריך ושעה לא תקינים");
            var st = new SystemTime { Year = (ushort)t.Year, Month = (ushort)t.Month, Day = (ushort)t.Day, Hour = (ushort)t.Hour, Minute = (ushort)t.Minute, Second = (ushort)t.Second };
            if (!SetLocalTime(ref st))
                throw BridgeException.Forbidden("שינוי השעה ידנית דורש הרשאות מנהל. אפשר לסנכרן מול האינטרנט או לשנות את אזור הזמן.");
        }
        return Get();
    }

    public static object Sync()
    {
        string o = Config.Run("w32tm", "/resync /nowait", 15000);
        bool denied = Regex.IsMatch(o ?? "", "0x80070005|denied|נדחתה", RegexOptions.IgnoreCase);
        if (denied) throw BridgeException.Forbidden("סנכרון השעה דורש הרשאות מנהל. Windows מסנכרן את השעה אוטומטית מעת לעת.");
        return Get();
    }
}

// ---------- שפת ברירת מחדל למקלדת ----------

/// <summary>שפת ההקלדה שנבחרת בכל חלון חדש ובכניסה למערכת (Set-WinDefaultInputMethodOverride)</summary>
public static class InputDefault
{
    private static readonly Regex TipRe = new(@"^[0-9A-Fa-f]{4}:[0-9A-Fa-f{}\-]{8,80}$");

    private static string Ps(string cmd) =>
        Config.Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + cmd + "\"", 20000);

    public static object Get()
    {
        var list = new List<object>();
        string raw = Ps("$lst = Get-WinUserLanguageList; for ($i = 0; $i -lt $lst.Count; $i++) { $l = $lst[$i]; foreach ($t in @($l.InputMethodTips)) { $l.LanguageTag + '|' + $l.LocalizedName + '|' + $t } }; '---'; (Get-WinDefaultInputMethodOverride).InputMethodTip");
        var parts = (raw ?? "").Replace("\r", "").Split("---");
        foreach (var line in parts[0].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|');
            if (f.Length == 3) list.Add(new { id = f[2].Trim(), lang = f[0].Trim(), name = f[1].Trim() });
        }
        string current = parts.Length > 1 ? parts[1].Trim() : "";
        return new { methods = list, current };
    }

    public static object Set(string id)
    {
        if (string.IsNullOrEmpty(id)) Ps("Set-WinDefaultInputMethodOverride");
        else
        {
            if (!TipRe.IsMatch(id)) throw BridgeException.BadRequest("מזהה שפה לא תקין");
            Ps("Set-WinDefaultInputMethodOverride -InputTip '" + id + "'");
        }
        return Get();
    }
}
