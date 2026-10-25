namespace OtzariaBridge;

/// <summary>#70: הערות ידניות מתוספים (סייר, הגדרות, צופה PDF וכו') נשלחות גם ל-GitHub דרך המתווך.
/// גוף: { text, plugin, version, repo? } — repo חייב להיות ברשימה הסגורה של המתווך.</summary>
internal static class GhFeedback
{
    public static void Register() => Server.Route("/sys/feedback", b =>
    {
        string text = b.Req("text");
        if (text.Length > 5000) text = text[..5000];
        string repo = System.Text.RegularExpressions.Regex.Replace(b.Str("repo", ""), @"[^A-Za-z0-9_.\-/]", "");
        string meta = "#meta kind=manual;repo=" + repo + ";plugin=" + b.Str("plugin", "").Replace(";", " ") + ";pv=" + b.Str("version", "").Replace(";", " ");
        GhRelay.Queue("fb-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), meta + "\n\n" + text);
        GhRelay.Kick();
        return new { queued = true };
    });
}
