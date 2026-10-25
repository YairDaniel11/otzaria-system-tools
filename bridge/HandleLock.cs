using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OtzariaBridge;

/// <summary>
/// נעילת handle בזמן נעילה פעילה (X3): ה-exe המותקן נפתח ללא FILE_SHARE_DELETE/WRITE, ותיקיית הנתונים ללא FILE_SHARE_DELETE.
/// WHY: exe רץ אמנם לא ניתן למחיקה, אבל ניתן לשינוי שם (ואז להחליף בקובץ אחר); ותיקייה ניתנת למחיקה/שינוי שם כולה. handle פתוח
/// בלי שיתוף מחיקה חוסם את שתיהן, גם ממנהל, עד שהתהליך משחרר או מת.
/// לא נעולים קבצי נתונים בודדים (config.json נכתב על ידינו) ותיקייה נפתחת עם Read|Write כדי שיצירת קבצים בה תמשיך לעבוד.
/// שחרור: Release() ב-Uninstall/Install/עדכון, וסנכרון כל 5 שניות עם Kiosk.IsActive (--unlock משחרר תוך ~5ש'). הריגת התהליך משחררת הכול.
/// </summary>
internal static class HandleLock
{
    private static readonly object G = new();
    private static SafeFileHandle _exe, _dir;
    private static System.Threading.Timer _timer;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, BACKUP_SEMANTICS = 0x02000000;

    internal static bool Held { get { lock (G) return _exe != null || _dir != null; } }

    /// <summary>פותח את ה-handles (idempotent). קובץ: Read בלבד. תיקייה: Read|Write בלי Delete</summary>
    internal static void Acquire(string exePath, string dirPath)
    {
        lock (G)
        {
            if (_exe == null && !string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var h = CreateFileW(exePath, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) h.Dispose(); else _exe = h;
            }
            if (_dir == null && !string.IsNullOrEmpty(dirPath) && Directory.Exists(dirPath))
            {
                var h = CreateFileW(dirPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, BACKUP_SEMANTICS, IntPtr.Zero);
                if (h.IsInvalid) h.Dispose(); else _dir = h;
            }
        }
    }

    internal static void Release()
    {
        lock (G) { _exe?.Dispose(); _exe = null; _dir?.Dispose(); _dir = null; }
    }

    /// <summary>החלטה טהורה: נועלים רק כשהנעילה פעילה</summary>
    internal static bool ShouldHold(bool kioskActive) => kioskActive;

    /// <summary>מסנכרן את הנעילה למצב ה-Kiosk כל 5 שניות. ה-exe הנעול הוא זה שרץ (Environment.ProcessPath)</summary>
    internal static void StartSync()
    {
        _timer ??= new System.Threading.Timer(_ =>
        {
            try
            {
                if (ShouldHold(Kiosk.IsActive)) Acquire(Environment.ProcessPath, Config.Dir);
                else Release();
            }
            catch { }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }
}
