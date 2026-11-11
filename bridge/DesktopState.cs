using System;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// דיווח 10.11 (פורום): אחרי הנעילה נעלמה תמונת הרקע וסמלי שולחן העבודה התפזרו. הסיבה הסבירה: בזמן נעילה ה-watchdog הורג את
/// explorer כל 2 שניות (הריגה, לא סגירה מסודרת), וכל עלייה מחדש קוראת את סידור הסמלים/הרקע מהרישום ויכולה לדרוס אותו.
/// לכן לפני תחילת הנעילה שומרים את ערכי הרישום של שולחן העבודה (סידור סמלים ב-Shell\Bags\1\Desktop, ערכי הרקע),
/// ובשחרור מחזירים אותם במדויק (בזמן ש-explorer כבוי, כדי שלא ידרוס) ורק אז מפעילים אותו.
/// הגיבוי נשמר ברישום ולכן שורד קריסה; הוא לא נדרס בגיבוי חדש כל עוד לא שוחזר.
/// </summary>
internal static class DesktopState
{
    internal const string BagsPath = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    internal const string WallpaperPath = @"Control Panel\Desktop";
    internal static readonly string[] WallpaperValues = { "Wallpaper", "WallpaperStyle", "TileWallpaper" };
    internal const string DefaultBackupPath = @"Software\OtzariaBridge\DesktopBackup";

    internal static bool HasBackup(string backupPath = DefaultBackupPath)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(backupPath); return k?.GetValue("done") != null; }
        catch { return false; }
    }

    /// <summary>שומר פעם אחת (לא דורס גיבוי קיים שעדיין לא שוחזר). מחזיר true אם נשמר עכשיו</summary>
    internal static bool Backup(string bagsPath = BagsPath, string wallpaperPath = WallpaperPath, string backupPath = DefaultBackupPath)
    {
        try
        {
            if (HasBackup(backupPath)) return false;
            Registry.CurrentUser.DeleteSubKeyTree(backupPath, false);
            using var root = Registry.CurrentUser.CreateSubKey(backupPath);
            using (var src = Registry.CurrentUser.OpenSubKey(bagsPath))
            using (var dst = root.CreateSubKey("Bags"))
                if (src != null) CopyValues(src, dst, null);
            using (var src = Registry.CurrentUser.OpenSubKey(wallpaperPath))
            using (var dst = root.CreateSubKey("Wallpaper"))
                if (src != null) CopyValues(src, dst, WallpaperValues);
            root.SetValue("done", 1, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) { Config.Log("desktop backup: " + ex.Message); return false; }
    }

    /// <summary>מחזיר את הערכים השמורים ומוחק את הגיבוי. לקרוא כש-explorer כבוי. מחזיר true אם שוחזר</summary>
    internal static bool Restore(string bagsPath = BagsPath, string wallpaperPath = WallpaperPath, string backupPath = DefaultBackupPath)
    {
        try
        {
            if (!HasBackup(backupPath)) return false;
            using (var root = Registry.CurrentUser.OpenSubKey(backupPath))
            {
                using (var src = root.OpenSubKey("Bags"))
                    if (src != null && src.ValueCount > 0)
                    {
                        // מוחקים ערכים שנוצרו אחרי הגיבוי, כדי שהסידור יהיה בדיוק כמו שהיה
                        using var dst = Registry.CurrentUser.CreateSubKey(bagsPath);
                        foreach (var n in dst.GetValueNames()) if (src.GetValue(n) == null) dst.DeleteValue(n, false);
                        CopyValues(src, dst, null);
                    }
                using (var src = root.OpenSubKey("Wallpaper"))
                    if (src != null)
                    {
                        using var dst = Registry.CurrentUser.CreateSubKey(wallpaperPath);
                        CopyValues(src, dst, WallpaperValues);
                    }
            }
            Registry.CurrentUser.DeleteSubKeyTree(backupPath, false);
            return true;
        }
        catch (Exception ex) { Config.Log("desktop restore: " + ex.Message); return false; }
    }

    private static void CopyValues(RegistryKey src, RegistryKey dst, string[] only)
    {
        foreach (var n in only ?? src.GetValueNames())
        {
            var v = src.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v == null) continue;
            dst.SetValue(n, v, src.GetValueKind(n));
        }
    }
}
