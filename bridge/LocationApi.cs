using System.Text.Json;
using Windows.Devices.Geolocation;

namespace OtzariaBridge;

/// <summary>מיקום מ-Windows Location (Windows.Devices.Geolocation), ו-/feedback ככינוי ל-/sys/feedback.
/// בתוך אוצריא navigator.geolocation נדחה, ולכן תוספים (כיוון תפילה, זמנים) מבקשים את המיקום מהגשר.
/// POST /sys/location (וגם /location): { maxAgeSec?: 600, timeoutSec?: 15 }
///   → { lat, lon, accuracy (מטרים), source ("gnss"|"wifi"|"cellular"|"ip"|"satellite"|"default"|"unknown"), time }
/// שירות כבוי או ללא הרשאה → שגיאה location_off עם הסבר איך להפעיל.</summary>
internal static class LocationApi
{
    private const string HowTo =
        "המיקום ב-Windows כבוי או חסום. להפעלה: הגדרות ← פרטיות ואבטחה ← מיקום: להפעיל את \"שירותי מיקום\" ואת \"אפשר לאפליקציות שולחן עבודה לגשת למיקום\". " +
        "Windows location is off. Turn it on in Settings > Privacy & security > Location (Location services + Let desktop apps access your location).";

    public static void Register()
    {
        Server.Route("/sys/location", Get);
        Server.Route("/location", Get);
        Server.Route("/sys/location/settings", _ =>
        {
            // F2 WHY: ms-settings בזמן נעילה = פתח לחלון הגדרות Windows (עוקף את שער SectionRoutes). הנעילה סוגרת אותו בכל מקרה, אז לא פותחים בכלל
            if (Kiosk.IsActive) throw BridgeException.Locked("פתיחת הגדרות המיקום חסומה במצב נעילה");
            OpenSettings(); return new { opened = true };
        });
        Server.Route("/feedback", b =>
        {
            // אותו פורמט כמו /sys/feedback (GhFeedback.cs): { text, plugin, version, repo? }
            string text = b.Req("text");
            if (text.Length > 5000) text = text[..5000];
            string repo = System.Text.RegularExpressions.Regex.Replace(b.Str("repo", ""), @"[^A-Za-z0-9_.\-/]", "");
            string meta = "#meta kind=" + (b.Str("kind", "manual") == "postcode" ? "postcode" : "manual") + ";repo=" + repo
                        + ";plugin=" + b.Str("plugin", "").Replace(";", " ") + ";pv=" + b.Str("version", "").Replace(";", " ");
            string title = b.Str("title", "");
            if (title.Length > 0) meta += ";title=" + title.Replace(";", " ").Replace("\n", " ")[..Math.Min(title.Length, 120)];
            GhRelay.Queue("fb-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), meta + "\n\n" + text);
            GhRelay.Kick();
            return new { queued = true };
        });
    }

    private static async Task<object> Get(JsonElement b)
    {
        int maxAge = (int)Math.Clamp(b.Long("maxAgeSec", 600), 0, 86400);
        int timeout = (int)Math.Clamp(b.Long("timeoutSec", 15), 3, 60);
        GeolocationAccessStatus access;
        try { access = await Geolocator.RequestAccessAsync(); }
        catch (Exception ex) { throw new BridgeException("location_unavailable", "שירות המיקום של Windows אינו זמין במחשב הזה: " + ex.Message, 503); }
        if (access != GeolocationAccessStatus.Allowed)
            throw new BridgeException("location_off", HowTo, 403);
        try
        {
            var g = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var p = await g.GetGeopositionAsync(TimeSpan.FromSeconds(maxAge), TimeSpan.FromSeconds(timeout));
            var c = p.Coordinate;
            return new
            {
                lat = Math.Round(c.Point.Position.Latitude, 6),
                lon = Math.Round(c.Point.Position.Longitude, 6),
                accuracy = Math.Round(c.Accuracy),
                source = c.PositionSource switch
                {
                    PositionSource.Satellite => "gnss",
                    PositionSource.WiFi => "wifi",
                    PositionSource.Cellular => "cellular",
                    PositionSource.IPAddress => "ip",
                    PositionSource.Default => "default",
                    _ => "unknown",
                },
                time = c.Timestamp.ToString("o"),
                // #105: רק במחשב עם GPS; אחרת null (גובה במטרים, כיוון תנועה במעלות מהצפון האמיתי, מהירות במ׳/ש׳)
                altitude = c.Point.Position.Altitude is double alt && alt != 0 ? Math.Round(alt, 1) : (double?)null,
                heading = c.Heading is double hd && !double.IsNaN(hd) ? Math.Round(hd, 1) : (double?)null,
                speed = c.Speed is double sp && !double.IsNaN(sp) ? Math.Round(sp, 2) : (double?)null,
            };
        }
        catch (UnauthorizedAccessException) { throw new BridgeException("location_off", HowTo, 403); }
        catch (Exception ex) when (ex is TimeoutException || ex is TaskCanceledException || (uint)ex.HResult == 0x800705B4)
        { throw new BridgeException("location_timeout", "המיקום לא התקבל בזמן. נסו שוב, או בדקו שהמיקום פועל ב-Windows.", 504); }
    }

    private static void OpenSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }); } catch { }
    }
}
