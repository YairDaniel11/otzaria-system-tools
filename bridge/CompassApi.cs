using Windows.Devices.Sensors;

namespace OtzariaBridge;

/// <summary>מצפן המכשיר (Windows.Devices.Sensors.Compass) — לטאבלטים/מחשבים עם חיישן מגנטי.
/// בתוך אוצריא deviceorientation לא מגיע ב-Windows, ולכן "כיוון תפילה" מבקש את כיוון הצפון מהגשר.
/// POST /sys/compass → { available, headingTrue (מעלות מצפון אמיתי, או null), headingMagnetic, accuracy ("high"|"approximate"|"unreliable"|"unknown"), time }</summary>
internal static class CompassApi
{
    private static Compass _c;
    private static bool _tried;
    private static readonly object Gate = new();

    public static void Register()
    {
        Server.Route("/sys/compass", _ => Read());
        Server.Route("/compass", _ => Read());
    }

    private static object Read()
    {
        lock (Gate)
        {
            if (!_tried) { _tried = true; try { _c = Compass.GetDefault(); if (_c != null) _c.ReportInterval = Math.Max(_c.MinimumReportInterval, 100u); } catch { _c = null; } }
        }
        if (_c == null) return new { available = false, headingTrue = (double?)null, headingMagnetic = (double?)null, accuracy = "unknown", time = (string)null };
        try
        {
            var r = _c.GetCurrentReading();
            if (r == null) return new { available = true, headingTrue = (double?)null, headingMagnetic = (double?)null, accuracy = "unknown", time = (string)null };
            string acc = r.HeadingAccuracy switch
            {
                MagnetometerAccuracy.High => "high", MagnetometerAccuracy.Approximate => "approximate",
                MagnetometerAccuracy.Unreliable => "unreliable", _ => "unknown",
            };
            return new { available = true, headingTrue = r.HeadingTrueNorth, headingMagnetic = (double?)r.HeadingMagneticNorth, accuracy = acc, time = r.Timestamp.ToString("o") };
        }
        catch (Exception ex) { throw new BridgeException("unsupported", "המצפן אינו זמין: " + ex.Message, 503); }
    }
}
