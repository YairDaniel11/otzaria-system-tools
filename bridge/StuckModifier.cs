using System.Runtime.InteropServices;

namespace OtzariaBridge;

/// <summary>תוצאת מעקב אחרי מקש-שינוי: לא לחוץ / לחוץ באמת / תקוע (key-up אבד)</summary>
internal enum StuckVerdict { NotDown, Held, Stuck }

/// <summary>
/// מקש-שינוי ש"המכונה חושבת שלחוץ" (GetAsyncKeyState) כי ה-key-up שלו לא הגיע: מקלדת מסך שננעלה, שליטה מרחוק ששלחה down בלי up,
/// שולחן מאובטח. WHY: ב-KioskKeys.ShouldBlock כל מקש נבלע כש-Win "לחוץ", וגם ה-key-up של Win נבלע, ולכן אי אפשר היה לנקות => מקלדת מתה.
/// הלוגיקה כאן טהורה (זמן מוזרק) לבדיקות xunit; ההזרקה עצמה ב-StuckInput.
/// Ctrl/Alt/Shift נחשבים תקועים אחרי 3 מקשים רגילים ו-5 שניות (ללחיצה אמיתית אין כזה דבר); Win אחרי המקש הרגיל הראשון
/// (Win אמיתי נבלע ב-hook ולכן אינו נראה ב-GetAsyncKeyState בכלל).
/// </summary>
internal sealed class StuckModifier
{
    internal static readonly byte[] CtrlVks = { 0xA2, 0xA3 };
    internal static readonly byte[] AltVks = { 0xA4, 0xA5 };     // לא VK_MENU הכללי
    internal static readonly byte[] ShiftVks = { 0xA0, 0xA1 };
    internal static readonly byte[] WinVks = { 0x5B, 0x5C };

    internal readonly string Name;
    internal readonly byte[] Vks;
    private readonly int _keys, _ms;
    private long _since = -1;
    private int _others;

    internal StuckModifier(string name, byte[] vks, int keys, int ms) { Name = name; Vks = vks; _keys = keys; _ms = ms; }

    internal static StuckModifier Ctrl() => new("Ctrl", CtrlVks, 3, 5000);
    internal static StuckModifier Alt() => new("Alt", AltVks, 3, 5000);
    internal static StuckModifier Shift() => new("Shift", ShiftVks, 3, 5000);
    internal static StuckModifier Win() => new("Win", WinVks, 1, 0);

    internal void Reset() { _since = -1; _others = 0; }

    /// <summary>
    /// down = מה ש-GetAsyncKeyState אומר; ownKeyDown = האירוע הוא ה-down של המקש עצמו (גם auto-repeat, מאפס את הטיימר);
    /// countable = מקש רגיל (המשתמש מקליד בזמן שהמקש "לחוץ").
    /// </summary>
    internal StuckVerdict Track(bool down, bool ownKeyDown, bool countable, long nowMs)
    {
        if (!down) { Reset(); return StuckVerdict.NotDown; }
        if (ownKeyDown || _since < 0)
        {
            _since = nowMs; _others = countable ? 1 : 0;
            if (ownKeyDown) return StuckVerdict.Held;
        }
        else if (countable) _others++;
        if (_others < _keys || nowMs - _since < _ms) return StuckVerdict.Held;
        Reset();
        return StuckVerdict.Stuck;
    }
}

/// <summary>סימון הזרקה ושחרור מקשים תקועים (לוגיקה טהורה + SendInput)</summary>
internal static class StuckInput
{
    /// <summary>dwExtraInfo על כל מה שהקיוסק מזריק: "KWOT"</summary>
    internal static readonly IntPtr Magic = new(0x544F574B);
    internal const uint LlkhfInjected = 0x10;
    internal const byte VkNoName = 0xFC;            // מקש שהוא כלום לאף אחד
    internal const long ArmMs = 1500;               // חלון שבו הזרקה מסומנת עוברת את ה-hook
    internal const long IdleModMs = 10_000, IdleWinMs = 5_000;

    internal static bool IsModifierVk(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    /// <summary>
    /// האם אירוע מקלדת הוא שחרור שהקיוסק עצמו הזריק ויש להעבירו הלאה בלי בדיקות. WHY (צר בכוונה): כל תהליך יכול לשלוח Magic,
    /// לכן עוברים רק (א) מוזרק, (ב) בתוך חלון ה-arm שנפתח ע"י שחרור אמיתי, (ג) מקש-שינוי up או מקש ה"כלום" - לעולם לא Win down.
    /// </summary>
    internal static bool IsPassable(IntPtr extra, uint flags, uint vk, bool down, long armedUntilMs, long nowMs)
    {
        if (extra != Magic || (flags & LlkhfInjected) == 0 || nowMs > armedUntilMs) return false;
        return vk == VkNoName || (!down && IsModifierVk(vk));
    }

    /// <summary>הזמן ללא אירוע מקלדת שאחריו מקש-שינוי "לחוץ" נחשב תקוע (Win קצר: אין לו מצב לחוץ לגיטימי)</summary>
    internal static bool IdleStuck(bool asyncDown, bool isWin, long lastEventMs, long nowMs) =>
        asyncDown && lastEventMs > 0 && nowMs - lastEventMs >= (isWin ? IdleWinMs : IdleModMs);

    /// <summary>
    /// רצף ההזרקה: Alt/Win מקבלים קודם מקש "כלום" (up בודד של Alt פותח תפריט, של Win פותח את תפריט התחל); אחר כך up רק לצדדים שלחוצים.
    /// </summary>
    internal static List<(byte Vk, bool Up)> BuildRelease(bool needsNoName, IEnumerable<byte> downSides)
    {
        var l = new List<(byte, bool)>();
        var sides = downSides.ToList();
        if (sides.Count == 0) return l;
        if (needsNoName) { l.Add((VkNoName, false)); l.Add((VkNoName, true)); }
        foreach (var v in sides) l.Add((v, true));
        return l;
    }

    internal static bool NeedsNoName(StuckModifier m) => ReferenceEquals(m.Vks, StuckModifier.WinVks) || ReferenceEquals(m.Vks, StuckModifier.AltVks);

    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    internal static void Inject(List<(byte Vk, bool Up)> seq)
    {
        if (seq.Count == 0) return;
        var arr = seq.Select(s => new INPUT
        {
            type = 1,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = s.Vk, dwFlags = s.Up ? 0x0002u : 0, dwExtraInfo = Magic } }
        }).ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }
}
