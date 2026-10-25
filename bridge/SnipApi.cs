using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Windows.Storage;
using Windows.System;

namespace OtzariaBridge;

/// <summary>
/// נקודות הקצה /snip/* — זיהוי טקסט דרך "פעולות טקסט" של כלי החיתוך של Windows.
/// אין לכלי החיתוך ממשק שמקבל תמונה ומחזיר טקסט, ולכן הגשר פותח את התמונה בכלי החיתוך
/// ומפעיל אותו כמו משתמש (UI Automation דרך PowerShell). תמונה אחת בכל פעם.
/// עדכון הסקריפט בלי בנייה מחדש: לשים קובץ snip-text.ps1 בתיקייה %LOCALAPPDATA%\OtzariaBridge.
/// </summary>
public static class SnipApi
{
    private const string Pfn = "Microsoft.ScreenSketch_8wekyb3d8bbwe";
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static void Register()
    {
        Server.Route("/snip/status", _ => Status());
        Server.Route("/snip/ocr", Ocr);
        Server.Route("/snip/dump", Dump);
    }

    private static string OverridePath => Path.Combine(Config.Dir, "snip-text.ps1");
    private static bool Running() => Process.GetProcessesByName("SnippingTool").Length > 0;

    private static object Status()
    {
        bool? installed = null;
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            installed = pm.FindPackagesForUser("", Pfn).Any();
        }
        catch { /* לא ידוע */ }
        return new { installed, running = Running(), scriptOverride = !LockedDown && File.Exists(OverridePath) };
    }

    private static byte[] DecodeImage(string s)
    {
        int comma = s.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) s = s.Substring(comma + 7);
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw BridgeException.BadRequest("השדה image אינו base64 תקין"); }
    }

    /// <summary>גוף: { image: base64 או data URL (PNG), timeoutSec?: 25 } → { text }</summary>
    private static async Task<object> Ocr(JsonElement b)
    {
        Programs.GrantForegroundGrace(); // כלי החיתוך נפתח כאן בלי דרך Programs.Activate - בלי חסד Watch מחזיר את אוצריא לחזית
        byte[] bytes = DecodeImage(b.Req("image"));
        if (bytes.Length == 0) throw BridgeException.BadRequest("התמונה ריקה");
        int timeout = (int)Math.Clamp(b.Long("timeoutSec", 25), 8, 90);
        if (!await OneAtATime.WaitAsync(0))
            throw new BridgeException("busy", "זיהוי דרך כלי החיתוך כבר רץ", 409);
        string dir = Path.Combine(Config.Dir, "snip");
        string id = Guid.NewGuid().ToString("N");
        string png = Path.Combine(dir, id + ".png");
        string outFile = Path.Combine(dir, id + ".json");
        try
        {
            if (Running())
                throw new BridgeException("busy", "כלי החיתוך כבר פתוח — סגרו אותו ונסו שוב", 409);
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(png, bytes);
            await Launch(png);
            var res = await RunScript(outFile, timeout, dump: false);
            if (!res.Bool("ok"))
                throw new BridgeException("snip_failed", res.Str("error") ?? "הפעלת כלי החיתוך נכשלה", 502);
            return new { text = res.Str("text") ?? "", engine = "snipping-tool" };
        }
        finally
        {
            try { File.Delete(png); } catch { }
            try { File.Delete(outFile); } catch { }
            OneAtATime.Release();
        }
    }

    /// <summary>אבחון: רשימת הרכיבים בחלון כלי החיתוך הפתוח (יש לפתוח אותו קודם עם תמונה)</summary>
    private static async Task<object> Dump(JsonElement b)
    {
        string outFile = Path.Combine(Config.Dir, "snip-dump.json");
        Directory.CreateDirectory(Config.Dir);
        var res = await RunScript(outFile, 10, dump: true);
        if (!res.Bool("ok"))
            throw new BridgeException("snip_failed", res.Str("error") ?? "האבחון נכשל", 502);
        return JsonSerializer.Deserialize<object>(res.GetRawText());
    }

    private static async Task Launch(string path)
    {
        Exception err = null;
        bool ok = false;
        var done = new TaskCompletionSource<bool>();
        Tray.Invoke(async () =>
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var opt = new LauncherOptions { TargetApplicationPackageFamilyName = Pfn };
                ok = await Launcher.LaunchFileAsync(file, opt);
            }
            catch (Exception ex) { err = ex; }
            done.TrySetResult(ok);
        });
        await done.Task;
        if (!ok)
            throw new BridgeException("unsupported",
                "לא ניתן לפתוח את כלי החיתוך" + (err != null ? " (" + err.Message + ")" : "") +
                ". ודאו שכלי החיתוך (Snipping Tool) מותקן ב-Windows 11.", 501);
    }

    /// <summary>
    /// נעילה/שירות/מצב מוגבל: אסור להריץ שום קוד שמקורו בתיקיית משתמש כתיבה.
    /// WHY: הגשר תהליך מהימן ל-AppTrust; הרצת snip-text.ps1 מ-%LOCALAPPDATA% (עם Bypass) אפשרה לילד להניח סקריפט ולהריץ קוד דרך אב מהימן.
    /// </summary>
    internal static bool LockedDown => Kiosk.IsActive || HardLock.Available || FxPolicy.IsRestricted;

    /// <summary>טהור לבדיקה: בנעילה תמיד הסקריפט המוטמע (override מתעלמים); אחרת override אם קיים</summary>
    internal static string PickScript(bool lockedDown, string overrideText, string embedded) =>
        !lockedDown && !string.IsNullOrWhiteSpace(overrideText) ? overrideText : embedded;

    private static string ScriptText()
    {
        string ov = null;
        try { if (!LockedDown && File.Exists(OverridePath)) ov = File.ReadAllText(OverridePath, Encoding.UTF8); } catch { }
        return PickScript(LockedDown, ov, Script);
    }

    /// <summary>
    /// שורת הפקודה: הסקריפט מועבר כטקסט ב-EncodedCommand (לא כקובץ ולא Bypass): אין קובץ להחליף במרוץ, וגם מדיניות ההרצה של המחשב נשמרת.
    /// הפרמטרים עוברים לבלוק הסקריפט כארגומנטים עם מרכאות בודדות מוכפלות.
    /// </summary>
    internal static string BuildEncodedCommand(string script, string outFile, int timeoutSec, bool dump)
    {
        string q = "'" + outFile.Replace("'", "''") + "'";
        string cmd = "& {" + (char)10 + script + (char)10 + "} -Out " + q + " -TimeoutSec " + timeoutSec + (dump ? " -Dump" : "");
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd));
    }

    private static async Task<JsonElement> RunScript(string outFile, int timeoutSec, bool dump)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        string enc = BuildEncodedCommand(ScriptText(), outFile, timeoutSec, dump);
        if (enc.Length > 30000) throw new BridgeException("snip_failed", "הסקריפט ארוך מדי להרצה", 500);
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand", enc })
            psi.ArgumentList.Add(a);
        try { File.Delete(outFile); } catch { }

        using var p = Process.Start(psi) ?? throw new BridgeException("unsupported", "לא ניתן להפעיל PowerShell", 501);
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEndAsync().Observe("snip stdout", quiet: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec + 20));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new BridgeException("timeout", "כלי החיתוך לא הגיב בזמן", 408);
        }
        if (!File.Exists(outFile))
        {
            string e = (await stderr).Trim();
            Config.Log("snip: אין פלט. " + e);
            throw new BridgeException("snip_failed", "הסקריפט לא החזיר תוצאה" + (e.Length > 0 ? ": " + e : ""), 502);
        }
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outFile, Encoding.UTF8));
        return doc.RootElement.Clone();
    }

    private const string Script = """"
# snip-text.ps1 - מפעיל את "פעולות טקסט" של כלי החיתוך (UI Automation) ומחזיר את הטקסט שזוהה.
# הגשר פותח קודם את התמונה בכלי החיתוך, ואז מריץ את הסקריפט הזה.
# בדיקה ידנית: פתחו תמונה בכלי החיתוך, ואז:
#   powershell -STA -File snip-text.ps1 -Out C:\temp\r.json
#   powershell -STA -File snip-text.ps1 -Out C:\temp\d.json -Dump   (רשימת רכיבי החלון)
param(
  [Parameter(Mandatory = $true)][string]$Out,
  [int]$TimeoutSec = 25,
  [switch]$Dump
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$AE = [Windows.Automation.AutomationElement]
# ביטויים עם \uXXXX כדי שיעבדו גם אם הקובץ נשמר בלי BOM.
# "סרוק טקסט" (Scan text) / "פעולות טקסט" (Text actions)
$ID_OCR          = 'OcrOverlayButton'
$RX_OCR_BUTTON   = '\u05E1\u05E8\u05D5\u05E7\s*\u05D8\u05E7\u05E1\u05D8|\u05E4\u05E2\u05D5\u05DC\u05D5\u05EA\s*(\u05D4)?\u05D8\u05E7\u05E1\u05D8|Scan\s*text|Text\s*actions'
# "העתק את כל הטקסט" / "העתק הכל" / Copy all text
$RX_COPY_ALL     = '\u05D4\u05E2\u05EA\u05E7.*\u05D8\u05E7\u05E1\u05D8|\u05D4\u05E2\u05EA\u05E7\s*\u05D4\u05DB\u05DC|Copy\s*all'
# "אל תשמור" / "לא לשמור" / Don't save
$RX_DONT_SAVE    = '^(\u05D0\u05DC\s*\u05EA\u05E9\u05DE\u05D5\u05E8|\u05DC\u05D0\s*\u05DC\u05E9\u05DE\u05D5\u05E8|Don.?t\s*save)$'

function Write-Result($obj) {
  $json = $obj | ConvertTo-Json -Compress -Depth 5
  [IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding($false)))
}
function Fail($msg) { Write-Result @{ ok = $false; error = $msg }; exit 0 }

function Get-SnipWindow {
  $p = Get-Process -Name SnippingTool -ErrorAction SilentlyContinue |
       Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) { return $AE::FromHandle($p.MainWindowHandle) }
  $kids = $AE::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
  foreach ($k in $kids) {
    $n = $k.Current.Name
    if ($n -and $n -match 'Snipping Tool|כלי\s*החיתוך') { return $k }
  }
  return $null
}
function Wait-SnipWindow($sec) {
  $end = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $end) {
    $w = Get-SnipWindow
    if ($w) { return $w }
    Start-Sleep -Milliseconds 300
  }
  return $null
}
function Find-ByName($win, $regex) {
  $all = $win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $all) {
    $n = $e.Current.Name
    if ($n -and $n -match $regex) { return $e }
  }
  return $null
}
function Find-ById($win, $id) {
  $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $win.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
}
function Set-On($e) {
  $pat = $null
  if ($e.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) {
    if ($pat.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $pat.Toggle() }
    return $true
  }
  return (Press $e)
}
function Wait-OcrButton($sec) {
  $end = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $end) {
    $w = Get-SnipWindow
    if ($w) {
      $e = Find-ById $w $ID_OCR
      if (-not $e) { $e = Find-ByName $w $RX_OCR_BUTTON }
      if ($e) { return $e }
    }
    Start-Sleep -Milliseconds 300
  }
  return $null
}
function Get-ButtonNames {
  $w = Get-SnipWindow
  if (-not $w) { return '' }
  $names = @()
  foreach ($e in $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    if ($c.Name -and $c.Name.Trim().Length -gt 0 -and $c.ControlType.ProgrammaticName -match 'Button|Custom|MenuItem') { $names += ('{0}[{1}]' -f $c.Name.Trim(), $c.AutomationId) }
  }
  $t = ($names | Select-Object -Unique) -join '; '
  if ($t.Length -gt 900) { $t = $t.Substring(0, 900) }
  return $t
}
function Wait-ByName($regex, $sec) {
  $end = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $end) {
    $w = Get-SnipWindow
    if ($w) {
      $e = Find-ByName $w $regex
      if ($e) { return $e }
    }
    Start-Sleep -Milliseconds 300
  }
  return $null
}
function Press($e) {
  $pat = $null
  if ($e.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
  if ($e.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { $pat.Toggle(); return $true }
  if ($e.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
  return $false
}
function Close-Snip {
  try {
    $w = Get-SnipWindow
    if ($w) { $w.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
  } catch { }
  Start-Sleep -Milliseconds 800
  try {
    $w = Get-SnipWindow
    if ($w) {
      $b = Find-ByName $w $RX_DONT_SAVE
      if ($b) { Press $b | Out-Null; Start-Sleep -Milliseconds 600 }
    }
  } catch { }
  $end = (Get-Date).AddSeconds(3)
  while ((Get-Date) -lt $end -and (Get-Process -Name SnippingTool -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 300 }
  Get-Process -Name SnippingTool -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

try {
  if ($Dump) {
    $w = Wait-SnipWindow 5
    if (-not $w) { Fail 'כלי החיתוך לא פתוח. פתחו אותו עם תמונה ונסו שוב.' }
    $items = @()
    $all = $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
      $c = $e.Current
      $items += ('{0} | name="{1}" | id="{2}" | class="{3}"' -f $c.ControlType.ProgrammaticName, $c.Name, $c.AutomationId, $c.ClassName)
    }
    Write-Result @{ ok = $true; window = $w.Current.Name; items = $items }
    exit 0
  }

  $win = Wait-SnipWindow $TimeoutSec
  if (-not $win) { Fail 'חלון כלי החיתוך לא נפתח' }

  # סימון בלוח, כדי לזהות שהועתק טקסט חדש
  $sentinel = '__snip_' + [guid]::NewGuid().ToString('N')
  [Windows.Forms.Clipboard]::SetText($sentinel)

  # מפעילים "סרוק טקסט" (הכפתור OcrOverlayButton) ואז "העתק את כל הטקסט"
  $copy = $null
  $w0 = Get-SnipWindow
  if ($w0) { $copy = Find-ByName $w0 $RX_COPY_ALL }
  if (-not $copy) {
    $ta = Wait-OcrButton ([Math]::Max(6, $TimeoutSec / 2))
    if (-not $ta) { $n = Get-ButtonNames; Close-Snip; Fail ('לא נמצא הכפתור "סרוק טקסט" בכלי החיתוך. כפתורים: ' + $n) }
    if (-not (Set-On $ta)) { Close-Snip; Fail 'לא ניתן להפעיל את "סרוק טקסט"' }
    $copy = Wait-ByName $RX_COPY_ALL 8
  }
  if ($copy) {
    if (-not (Press $copy)) { Close-Snip; Fail 'לא ניתן ללחוץ על "העתק את כל הטקסט"' }
  } else {
    # גיבוי: בחירת כל הטקסט בשכבת הזיהוי והעתקה במקלדת
    try { (Get-SnipWindow).SetFocus() } catch { }
    Start-Sleep -Milliseconds 700
    [Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 300
    [Windows.Forms.SendKeys]::SendWait('^c')
  }

  $text = $null
  $names = Get-ButtonNames
  $end = (Get-Date).AddSeconds(6)
  while ((Get-Date) -lt $end) {
    Start-Sleep -Milliseconds 250
    try { $t = [Windows.Forms.Clipboard]::GetText() } catch { $t = $null }
    if ($t -and $t -ne $sentinel) { $text = $t; break }
  }
  Close-Snip
  if ($null -eq $text) { Fail ('הלוח לא התעדכן (לא נמצא "העתק את כל הטקסט" או שלא זוהה טקסט). כפתורים שנראו: ' + $names) }
  Write-Result @{ ok = $true; text = $text }
}
catch {
  try { Close-Snip } catch { }
  Fail ('שגיאה בסקריפט: ' + $_.Exception.Message)
}
"""";
}
