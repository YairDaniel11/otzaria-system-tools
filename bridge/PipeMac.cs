// #32 ממצא 1: חתימת HMAC קצרת-טווח לפקודות רגישות בצינור גשר↔שירות.
// סוד משותף (32 בתים) נוצר בגשר, נשמר מוצפן DPAPI (CurrentUser) ב-config, ונמסר לשירות פעם אחת
// (בהגדרה הראשונה, או בפקודת set-mac-key שדורשת סיסמה + Windows Hello טרי). מאז, כשהאימות הדו-שלבי פעיל,
// השירות מקבל פקודה רגישה רק עם: אישור Hello חד-פעמי (hello) + ts בחלון ±60ש' + mac=HMAC-SHA256(key, canonical(req)).
// כך אישור Hello שדלף אינו ניתן להצמדה לפקודה אחרת/מאוחרת, ותהליך שאינו מחזיק במפתח אינו יכול לנצל אותו.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

public static class PipeMac
{
    public const int WindowSeconds = 60;
    private const string CfgKey = "kiosk.hardMacKey";

    /// <summary>מחרוזת קנונית: כל השדות חוץ מ-mac, ממוינים, key=json בשורות</summary>
    public static string Canonical(JsonObject req) =>
        string.Join("\n", req.Where(kv => kv.Key != "mac").OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + (kv.Value?.ToJsonString() ?? "null")));

    public static string Compute(byte[] key, JsonObject req) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(req))));

    /// <summary>צד הגשר: מוסיף ts ו-mac (אם יש מפתח)</summary>
    public static void Sign(JsonObject req)
    {
        var key = LoadKey();
        if (key == null) return;
        req.Remove("mac");
        req["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        req["mac"] = Compute(key, req);
    }

    /// <summary>צד השירות: true אם ה-mac תקין וטרי</summary>
    public static bool Verify(byte[] key, JsonObject req, long nowUnix)
    {
        if (key == null || key.Length < 32) return false;
        string mac = req["mac"]?.ToString();
        long ts;
        try { ts = req["ts"]?.GetValue<long>() ?? 0; } catch { return false; }
        if (string.IsNullOrEmpty(mac) || Math.Abs(nowUnix - ts) > WindowSeconds) return false;
        byte[] got;
        try { got = Convert.FromBase64String(mac); } catch { return false; }
        byte[] want = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(req)));
        return CryptographicOperations.FixedTimeEquals(got, want);
    }

    public static byte[] LoadKey()
    {
        string s = Config.Get<string>(CfgKey, "");
        if (string.IsNullOrEmpty(s)) return null;
        try { return Unprotect(Convert.FromBase64String(s)); } catch { return null; }
    }

    /// <summary>מחזיר מפתח קיים או יוצר ושומר חדש</summary>
    public static byte[] EnsureKey()
    {
        var k = LoadKey();
        if (k != null && k.Length >= 32) return k;
        k = RandomNumberGenerator.GetBytes(32);
        Config.Set(CfgKey, Convert.ToBase64String(Protect(k)));
        Config.Save();
        return k;
    }

    // ---- DPAPI (CurrentUser) ----
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int cb; public IntPtr pb; }
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob i, string d, IntPtr e, IntPtr r, IntPtr p, int f, ref Blob o);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob i, IntPtr d, IntPtr e, IntPtr r, IntPtr p, int f, ref Blob o);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private static byte[] Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var i = new Blob { cb = data.Length, pb = h.AddrOfPinnedObject() };
            var o = new Blob();
            bool ok = protect ? CryptProtectData(ref i, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref o)
                              : CryptUnprotectData(ref i, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref o);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var r = new byte[o.cb]; Marshal.Copy(o.pb, r, 0, o.cb); LocalFree(o.pb);
            return r;
        }
        finally { h.Free(); }
    }
    private static byte[] Protect(byte[] d) => Run(d, true);
    private static byte[] Unprotect(byte[] d) => Run(d, false);
}
