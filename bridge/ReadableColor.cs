using System.Drawing;

namespace OtzariaBridge;

/// <summary>סבב A8: צבע ההדגשה מאוצריא עלול להיות כהה ולא נקרא על רקע שחור של שומר המסך —
/// מבהירים אותו (שומרים על הגוון) עד בהירות מינימלית.</summary>
internal static class ReadableColor
{
    public static Color OnDark(Color c, float minBright = 0.62f)
    {
        float b = (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;
        if (b >= minBright) return c;
        float t = b >= 0.99f ? 0 : (minBright - b) / (1f - b);
        int L(int v) => (int)(v + (255 - v) * t);
        return Color.FromArgb(c.A, L(c.R), L(c.G), L(c.B));
    }
}
