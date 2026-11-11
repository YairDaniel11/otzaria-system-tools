using Microsoft.Win32;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>שמירה/שחזור של סידור סמלי שולחן העבודה והרקע (מפתחות בדיקה זמניים ב-HKCU, לא נוגעים בערכים האמיתיים)</summary>
public class DesktopStateTests
{
    private static string Root() => @"Software\OtzariaBridgeTest\" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Backup_then_restore_returns_exact_values_and_removes_extras()
    {
        string r = Root(), bags = r + @"\Bags", wp = r + @"\Desk", bk = r + @"\Bk";
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(bags)) { k.SetValue("IconLayouts", new byte[] { 1, 2, 3, 250 }, RegistryValueKind.Binary); k.SetValue("Sort", 7, RegistryValueKind.DWord); }
            using (var k = Registry.CurrentUser.CreateSubKey(wp)) { k.SetValue("Wallpaper", @"C:\a.jpg"); k.SetValue("WallpaperStyle", "10"); k.SetValue("Other", "keep"); }

            Assert.True(DesktopState.Backup(bags, wp, bk));
            Assert.True(DesktopState.HasBackup(bk));

            using (var k = Registry.CurrentUser.OpenSubKey(bags, true)) { k.SetValue("IconLayouts", new byte[] { 9, 9 }, RegistryValueKind.Binary); k.SetValue("New", 1); k.DeleteValue("Sort"); }
            using (var k = Registry.CurrentUser.OpenSubKey(wp, true)) k.SetValue("Wallpaper", "");

            Assert.False(DesktopState.Backup(bags, wp, bk));   // לא דורס גיבוי קיים
            Assert.True(DesktopState.Restore(bags, wp, bk));
            Assert.False(DesktopState.HasBackup(bk));

            using (var k = Registry.CurrentUser.OpenSubKey(bags))
            {
                Assert.Equal(new byte[] { 1, 2, 3, 250 }, (byte[])k.GetValue("IconLayouts"));
                Assert.Equal(7, (int)k.GetValue("Sort"));
                Assert.Null(k.GetValue("New"));
            }
            using (var k = Registry.CurrentUser.OpenSubKey(wp))
            {
                Assert.Equal(@"C:\a.jpg", (string)k.GetValue("Wallpaper"));
                Assert.Equal("keep", (string)k.GetValue("Other"));
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(r, false); }
    }

    [Fact]
    public void Restore_without_backup_does_nothing()
    {
        string r = Root();
        Assert.False(DesktopState.Restore(r + @"\B", r + @"\W", r + @"\Bk"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(r + @"\B"));
    }
}
