using System;
using System.IO;

namespace OtzariaBridge;

/// <summary>סבב B1: רשומות שנשמרו בגרסאות קודמות בשם הישן ("תורת אסתר" וכו') או שמצביעות על הגשר עצמו — מוצגות בסרגל הצד כ"אחת שאלתי".</summary>
internal static class SideBarNames
{
    private static readonly string[] Old = { "תורת אסתר", "גמח תורת אסתר", "גשר אוצריא", "תוכנת חסימת אוצריא", "תוכנת חסימת אוצריא - גמח תורת אסתר", "OtzariaBridge" };

    public static string Fix(string name, string path)
    {
        string n = (name ?? "").Trim();
        bool self = false;
        try { self = !string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(path), "OtzariaBridge.exe", StringComparison.OrdinalIgnoreCase); } catch { }
        if (self || Array.IndexOf(Old, n) >= 0) return Lang.T(Program.AppName, Program.AppNameEn);
        return name;
    }
}
