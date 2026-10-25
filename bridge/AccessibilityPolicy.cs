using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// כיבוי קיצורי נגישות (5xShift/FilterKeys/ToggleKeys/MouseKeys/HighContrast) ומדיניות NoWinKeys/AutoPlay כל עוד הנעילה פעילה, ושחזור המקור.
/// WHY: 5xShift פותח דיאלוג עם קישור להגדרות מעל הנעילה; Win+מקש בתוכנה מורמת לא נראה ל-hook (UIPI) ו-NoWinKeys סוגר זאת;
/// חלון AutoPlay קופץ מעל מסך נעול. נכבה רק ביט ה-HOTKEYACTIVE (תכונה שבשימוש נשארת). המקור נשמר ב-HKCU ומשוחזר בשחרור,
/// ובעלייה בלי נעילה (Orphan) - אם הגשר קרס, המקשים נשארים כבויים (כיוון בטוח) עד השחזור הבא.
/// </summary>
internal static class AccessibilityPolicy
{
    internal const uint HotkeyActive = 0x4;   // SKF_/FKF_/TKF_/MKF_/HCF_HOTKEYACTIVE - אותו ביט בכולם
    private const string SaveKey = @"Software\OtzariaBridge\KioskSaved";
    private const string ExplorerPolicy = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    // ----- לוגיקה טהורה (נבדקת ב-xunit) -----
    internal static uint WithoutHotkey(uint flags) => flags & ~HotkeyActive;
    internal static bool NeedsDisable(uint flags) => (flags & HotkeyActive) != 0;
    /// <summary>מחזירים את הביט רק אם היה דלוק במקור ועכשיו כבוי (לא דורסים שינוי שהמשתמש עשה בינתיים)</summary>
    internal static uint? Restored(uint current, bool wasActive) => wasActive && (current & HotkeyActive) == 0 ? current | HotkeyActive : null;

    /// <summary>ערכי מדיניות לכתיבה בנעילה: NoDriveTypeAutoRun=0xFF (לא 1: זה רק "סוגים לא ידועים")</summary>
    internal static readonly (string Name, int Value)[] PolicyValues = { ("NoWinKeys", 1), ("NoDriveTypeAutoRun", 0xFF), ("NoAutorun", 1) };

    /// <summary>מה לעשות בשחזור ערך מדיניות: null = למחוק, אחרת להחזיר את הערך המקורי. לא נוגעים אם מישהו שינה אחרינו</summary>
    internal static (bool Touch, int? Value) PolicyRestore(int? original, int? current, int ours) =>
        current == ours ? (true, original) : (false, null);

    // ----- P/Invoke -----
    [StructLayout(LayoutKind.Sequential)] private struct Flags2 { public uint cbSize, dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct FilterKeys { public uint cbSize, dwFlags, iWaitMSec, iDelayMSec, iRepeatMSec, iBounceMSec; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseKeys { public uint cbSize, dwFlags, iMaxSpeed, iTimeToMaxSpeed, iCtrlSpeed, dwReserved1, dwReserved2; }
    [StructLayout(LayoutKind.Sequential)] private struct HighContrast { public uint cbSize, dwFlags; public IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool Spi(uint a, uint p, ref Flags2 v, uint ini);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool Spi(uint a, uint p, ref FilterKeys v, uint ini);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool Spi(uint a, uint p, ref MouseKeys v, uint ini);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool Spi(uint a, uint p, ref HighContrast v, uint ini);
    private const uint Persist = 0x01 | 0x02;

    private static uint? Get(string n, bool set, uint flags) => n switch
    {
        "sticky" => Do(new Flags2 { cbSize = 8 }, 0x3A, 0x3B, set, flags),
        "toggle" => Do(new Flags2 { cbSize = 8 }, 0x34, 0x35, set, flags),
        "filter" => DoF(set, flags),
        "mouse" => DoM(set, flags),
        _ => DoH(set, flags),
    };
    private static uint? Do(Flags2 v, uint g, uint s, bool set, uint flags)
    {
        if (!Spi(g, v.cbSize, ref v, 0)) return null;
        if (!set) return v.dwFlags;
        v.dwFlags = flags;
        return Spi(s, v.cbSize, ref v, Persist) ? flags : null;
    }
    private static uint? DoF(bool set, uint flags)
    {
        var v = new FilterKeys { cbSize = (uint)Marshal.SizeOf<FilterKeys>() };
        if (!Spi(0x32, v.cbSize, ref v, 0)) return null;
        if (!set) return v.dwFlags;
        v.dwFlags = flags;
        return Spi(0x33, v.cbSize, ref v, Persist) ? flags : null;
    }
    private static uint? DoM(bool set, uint flags)
    {
        var v = new MouseKeys { cbSize = (uint)Marshal.SizeOf<MouseKeys>() };
        if (!Spi(0x36, v.cbSize, ref v, 0)) return null;
        if (!set) return v.dwFlags;
        v.dwFlags = flags;
        return Spi(0x37, v.cbSize, ref v, Persist) ? flags : null;
    }
    private static uint? DoH(bool set, uint flags)
    {
        var v = new HighContrast { cbSize = (uint)Marshal.SizeOf<HighContrast>() };
        if (!Spi(0x42, v.cbSize, ref v, 0)) return null;
        if (!set) return v.dwFlags;
        v.dwFlags = flags;
        v.lpszDefaultScheme = IntPtr.Zero;
        return Spi(0x43, v.cbSize, ref v, Persist) ? flags : null;
    }

    private static readonly string[] Features = { "sticky", "toggle", "filter", "mouse", "contrast" };

    /// <summary>מכבה קיצורים וכותב מדיניות. אידמפוטנטי: הפעלה שנייה (בלי שחזור ביניהן) לא שומרת ערכים כבויים כ"מקור"</summary>
    internal static void Apply()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(SaveKey);
            bool saved = k.GetValue("saved") != null;
            foreach (var f in Features)
            {
                uint? cur = Get(f, false, 0);
                if (cur == null) continue;
                if (!saved) k.SetValue("a_" + f, NeedsDisable(cur.Value) ? 1 : 0, RegistryValueKind.DWord);
                if (NeedsDisable(cur.Value)) Get(f, true, WithoutHotkey(cur.Value));
            }
            using var p = Registry.CurrentUser.CreateSubKey(ExplorerPolicy);
            foreach (var (name, val) in PolicyValues)
            {
                if (!saved)
                {
                    object o = p.GetValue(name);
                    k.SetValue("p_" + name, o is int i ? i : -1, RegistryValueKind.DWord);   // -1 = לא היה קיים
                }
                p.SetValue(name, val, RegistryValueKind.DWord);
            }
            k.SetValue("saved", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Config.Log("accessibility policy apply: " + ex.Message); }
    }

    /// <summary>משחזר את המקור. בטוח לקריאה גם בלי שמירה קודמת (אז לא עושה כלום)</summary>
    internal static void Restore()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SaveKey, true))
            {
                if (k == null || k.GetValue("saved") == null) return;
                foreach (var f in Features)
                {
                    bool was = k.GetValue("a_" + f) is int a && a != 0;
                    uint? cur = Get(f, false, 0);
                    if (cur != null && Restored(cur.Value, was) is uint nv) Get(f, true, nv);
                }
                using var p = Registry.CurrentUser.OpenSubKey(ExplorerPolicy, true);
                if (p != null)
                    foreach (var (name, val) in PolicyValues)
                    {
                        int? orig = k.GetValue("p_" + name) is int o && o >= 0 ? o : null;
                        int? cur = p.GetValue(name) is int c ? c : null;
                        var (touch, v) = PolicyRestore(orig, cur, val);
                        if (!touch) continue;
                        if (v == null) p.DeleteValue(name, false); else p.SetValue(name, v.Value, RegistryValueKind.DWord);
                    }
            }
            Registry.CurrentUser.DeleteSubKeyTree(SaveKey, false);
        }
        catch (Exception ex) { Config.Log("accessibility policy restore: " + ex.Message); }
    }
}
