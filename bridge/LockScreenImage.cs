using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OtzariaBridge;

/// <summary>
/// משוב 15.10: תמונת מסך הנעילה של Windows = מסך המנוחה (פסוק, סמל, זמני היום, הקדשה).
/// kiosk.lockScreenImage (ברירת מחדל כבוי). מתעדכן כל 30 דקות (הזמנים משתנים) דרך
/// Windows.System.UserProfile.LockScreen.SetImageFileAsync — עובד למשתמש רגיל ב-Windows 10/11, "זרקור Windows" מכובה אוטומטית (HKCU).
/// שם קובץ מתחלף (a/b) כי Windows שומר במטמון לפי שם.
/// </summary>
internal static class LockScreenImage
{
    private static System.Threading.Timer _timer;
    private static bool _flip;

    public static void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Tray.Invoke(Update), null, 20_000, 30 * 60_000);
        // האפשרות כבויה אבל מסך הנעילה עדיין הוחלף (למשל התוכנה נסגרה לפני שבוטלה האפשרות בתוסף) — מחזירים את המקורי
        if (!Config.Get("kiosk.lockScreenImage", false) && Config.Get(BackupKey + ".done", false))
            SafeTask.Run(() => Tray.Invoke(Restore));
    }

    /// <summary>"זרקור Windows" מחליף את תמונת מסך הנעילה — מכבים אותו למשתמש הנוכחי (HKCU, בלי הרשאות מנהל)</summary>
    private static void DisableSpotlight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
            k.SetValue("RotatingLockScreenEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("RotatingLockScreenOverlayEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) { Config.Log("lockscreen spotlight: " + ex.Message); }
    }

    private const string BackupKey = "kiosk.lockScreenBackup";
    private const string CdmKey = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridge");

    private static int ReadCdm(string name)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(CdmKey);
            return k?.GetValue(name) is int v ? v : -1;
        }
        catch { return -1; }
    }

    /// <summary>
    /// שומר פעם אחת, לפני שמחליפים את מסך הנעילה, את מצבו של המשתמש: התמונה הנוכחית (עותק בתיקיית הגשר) וערכי "זרקור Windows".
    /// אם כבר הוחלפה תמונה בעבר (קבצי lockscreen-a/b קיימים) — לא יודעים מה המקור, ולכן לא שומרים תמונה (בשחזור יוחזר רק הזרקור).
    /// </summary>
    private static void BackupOriginal()
    {
        if (Config.Get(BackupKey + ".done", false)) return;
        try
        {
            bool alreadyOverridden = File.Exists(Path.Combine(Dir, "lockscreen-a.png")) || File.Exists(Path.Combine(Dir, "lockscreen-b.png"));
            string saved = "";
            if (!alreadyOverridden)
            {
                try
                {
                    string local = Windows.System.UserProfile.LockScreen.OriginalImageFile?.LocalPath ?? "";
                    if (local.Length > 0 && File.Exists(local))
                    {
                        string bdir = Path.Combine(Dir, "lockscreen-backup");
                        Directory.CreateDirectory(bdir);
                        string ext = Path.GetExtension(local);
                        saved = Path.Combine(bdir, "original" + (ext.Length > 0 ? ext : ".jpg"));
                        File.Copy(local, saved, true);
                    }
                }
                catch (Exception ex) { saved = ""; Config.Log("lockscreen backup image: " + ex.Message); }
            }
            Config.Set(BackupKey + ".image", saved);
            Config.Set(BackupKey + ".spot", alreadyOverridden ? 1 : ReadCdm("RotatingLockScreenEnabled"));
            Config.Set(BackupKey + ".overlay", alreadyOverridden ? 1 : ReadCdm("RotatingLockScreenOverlayEnabled"));
            Config.Set(BackupKey + ".done", true);
            Config.Save();
        }
        catch (Exception ex) { Config.Log("lockscreen backup: " + ex.Message); }
    }

    /// <summary>
    /// מחזיר את מסך הנעילה של Windows למה שהיה לפני התוכנה: התמונה ששמרנו (אם הייתה) וערכי "זרקור Windows".
    /// נקרא בביטול האפשרות בתוסף וביציאה מהתוכנה. אחריו הגיבוי מתאפס, כדי שהפעלה חוזרת תשמור את המצב העדכני.
    /// </summary>
    public static void Restore()
    {
        try
        {
            int spot = Config.Get(BackupKey + ".spot", 1), overlay = Config.Get(BackupKey + ".overlay", 1);
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(CdmKey);
                k.SetValue("RotatingLockScreenEnabled", spot >= 0 ? spot : 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("RotatingLockScreenOverlayEnabled", overlay >= 0 ? overlay : 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception ex) { Config.Log("lockscreen restore spotlight: " + ex.Message); }

            string img = Config.Get(BackupKey + ".image", "");
            // אין תמונה שמורה (למשל ההחלפה נעשתה לפני שהיה גיבוי) — חוזרים לתמונת ברירת המחדל של Windows, כדי שלא תישאר תמונת השומר
            if (img.Length == 0)
            {
                string web = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Screen");
                foreach (var cand in new[] { "img100.jpg", "img102.jpg", "img103.png", "img105.jpg" })
                    if (File.Exists(Path.Combine(web, cand))) { img = Path.Combine(web, cand); break; }
            }
            if (img.Length > 0 && File.Exists(img))
            {
                var t = Task.Run(async () =>
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(img);
                    await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
                });
                if (!t.Wait(6000)) Config.Log("lockscreen restore: timeout");
                else if (t.IsFaulted) Config.Log("lockscreen restore: " + t.Exception?.GetBaseException().Message);
            }
            foreach (var n in new[] { "lockscreen-a.png", "lockscreen-b.png" })
                try { File.Delete(Path.Combine(Dir, n)); } catch { }
            Config.Set(BackupKey + ".done", false);
            Config.Save();
            Config.Log("lockscreen: שוחזר מסך הנעילה" + (img.Length > 0 ? " (כולל התמונה הקודמת)" : " (בלי תמונה קודמת — נשמר רק הזרקור)"));
        }
        catch (Exception ex) { Config.Log("lockscreen restore: " + ex.Message); }
    }

    /// <summary>ביציאה מהתוכנה (לא בנעילה): אם הוחלפה תמונת מסך הנעילה — מחזירים את הקודמת. בהפעלה הבאה היא מוחלפת שוב</summary>
    public static void RestoreOnExit()
    {
        if (Config.Get(BackupKey + ".done", false) || File.Exists(Path.Combine(Dir, "lockscreen-a.png")) || File.Exists(Path.Combine(Dir, "lockscreen-b.png")))
            Restore();
    }

    public static void Update()
    {
        if (!Config.Get("kiosk.lockScreenImage", false)) return;
        BackupOriginal();
        try
        {
            var scr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            int w = Math.Max(1280, scr.Width), h = Math.Max(720, scr.Height);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridge");
            Directory.CreateDirectory(dir);
            _flip = !_flip;
            string path = Path.Combine(dir, "lockscreen-" + (_flip ? "a" : "b") + ".png");
            using (var bmp = new Bitmap(w, h))
            using (var g = Graphics.FromImage(bmp))
            using (var rs = new RestScreen { StillImage = true })
            {
                rs.Render(g, new Rectangle(0, 0, w, h), 0f, h / 1080f * 1.25f);
                bmp.Save(path, ImageFormat.Png);
            }
            DisableSpotlight();
            SafeTask.Run(async () =>
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
                }
                catch (Exception ex) { Config.Log("lockscreen: " + ex.Message); }
            });
        }
        catch (Exception ex) { Config.Log("lockscreen render: " + ex.Message); }
    }
}
