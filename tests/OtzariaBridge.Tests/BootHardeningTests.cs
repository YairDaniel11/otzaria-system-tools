using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>
/// הקשחת אתחול: פרסור פלט bcdedit/reagentc, אכיפה והחזרה (רק מה שאנחנו שינינו), וממצאי BitLocker/TPM/Secure Boot.
/// הכל מול host מזויף: שום bcdedit/reagentc/רישום אמיתי לא רץ.
/// </summary>
public class BootHardeningTests
{
    private sealed class FakeHost : BootHardening.IBootHost
    {
        public Dictionary<string, string> Bcd = new(StringComparer.OrdinalIgnoreCase)
        {
            ["recoveryenabled"] = "Yes", ["bootmenupolicy"] = "Standard",
        };
        public bool ReEnabled = true;
        public HashSet<string> SafeBoot = new();
        public string State;
        public List<string> Commands = new();
        public bool BcdSetFails;

        public string Run(string file, string args)
        {
            Commands.Add(file + " " + args);
            if (file == "reagentc.exe")
            {
                if (args == "/disable") ReEnabled = false;
                if (args == "/enable") ReEnabled = true;
                return "Windows RE status:         " + (ReEnabled ? "Enabled" : "Disabled") + "\r\nWindows RE location:       " +
                       (ReEnabled ? @"\\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE" : "");
            }
            if (args.StartsWith("/set {current} ") && !BcdSetFails)
            {
                var p = args.Split(' ');
                Bcd[p[2]] = p[3];
            }
            else if (args.StartsWith("/deletevalue {current} ")) Bcd.Remove(args.Split(' ')[2]);
            else if (args == "/enum {current}")
                return "Windows Boot Loader\r\n-------------------\r\nidentifier              {current}\r\n" +
                       string.Join("", Bcd.Select(kv => kv.Key.PadRight(24) + kv.Value + "\r\n"));
            return "The operation completed successfully.";
        }

        public bool SafeBootExists(string set, string name) => SafeBoot.Contains(set + "\\" + name);
        public bool SafeBootCreate(string set, string name) { SafeBoot.Add(set + "\\" + name); return true; }
        public bool SafeBootDelete(string set, string name) { SafeBoot.Remove(set + "\\" + name); return true; }
        public string ReadState() => State;
        public void WriteState(string json) => State = json;
        public void Log(string line) { }
    }

    [Fact]
    public void ParseBcdValue_reads_exact_field_and_missing_is_null()
    {
        string t = "identifier   {current}\r\nrecoveryenabled         No\r\nbootstatuspolicy        IgnoreAllFailures\r\n";
        Assert.Equal("No", BootHardening.ParseBcdValue(t, "recoveryenabled"));
        Assert.Equal("IgnoreAllFailures", BootHardening.ParseBcdValue(t, "bootstatuspolicy"));
        Assert.Null(BootHardening.ParseBcdValue(t, "bootmenupolicy"));
        Assert.Null(BootHardening.ParseBcdValue(null, "x"));
    }

    [Fact]
    public void LooksLikeBcdEntry_rejects_error_text()
    {
        Assert.False(BootHardening.LooksLikeBcdEntry("Access is denied."));
        Assert.False(BootHardening.LooksLikeBcdEntry(""));
        Assert.True(BootHardening.LooksLikeBcdEntry("identifier   {current}\r\n"));
    }

    [Fact]
    public void ParseReagentc_by_location_not_by_localized_word()
    {
        Assert.True(BootHardening.ParseReagentcEnabled(@"Windows RE location: \\?\GLOBALROOT\device\x"));
        Assert.False(BootHardening.ParseReagentcEnabled("Windows RE status: Disabled\r\nWindows RE location:"));
        Assert.Null(BootHardening.ParseReagentcEnabled(""));
        Assert.Null(BootHardening.ParseReagentcEnabled("garbage"));
    }

    [Fact]
    public void Enforce_active_applies_everything_and_records_originals()
    {
        var h = new FakeHost();
        var r = BootHardening.Enforce(h, true);
        Assert.False(r.Incomplete);
        Assert.Contains(@"Minimal\" + BootHardening.SafeBootName, h.SafeBoot);
        Assert.Contains(@"Network\" + BootHardening.SafeBootName, h.SafeBoot);
        Assert.False(h.ReEnabled);
        Assert.Equal("No", h.Bcd["recoveryenabled"]);
        Assert.Equal("IgnoreAllFailures", h.Bcd["bootstatuspolicy"]);
        Assert.Equal("Standard", h.Bcd["bootmenupolicy"]); // כבר היה: לא שונה ולא נרשם
        Assert.DoesNotContain(h.Commands, c => c.Contains("bootmenupolicy") && c.Contains("/set"));
    }

    [Fact]
    public void Release_restores_only_what_we_changed()
    {
        var h = new FakeHost();
        BootHardening.Enforce(h, true);
        var r = BootHardening.Enforce(h, false);
        Assert.False(r.Incomplete);
        Assert.Empty(h.SafeBoot);
        Assert.True(h.ReEnabled);
        Assert.Equal("Yes", h.Bcd["recoveryenabled"]);
        Assert.False(h.Bcd.ContainsKey("bootstatuspolicy")); // לא היה קיים: נמחק, לא נקבע לערך שרירותי
        Assert.Equal("Standard", h.Bcd["bootmenupolicy"]);
    }

    [Fact]
    public void Release_does_not_touch_things_that_were_already_hardened_before_us()
    {
        var h = new FakeHost { ReEnabled = false };
        h.Bcd["recoveryenabled"] = "No";
        h.SafeBoot.Add(@"Minimal\" + BootHardening.SafeBootName);
        BootHardening.Enforce(h, true);
        BootHardening.Enforce(h, false);
        Assert.False(h.ReEnabled);                       // כבוי מראש: לא מפעילים
        Assert.Equal("No", h.Bcd["recoveryenabled"]);    // כבר No מראש: לא מחזירים ל-Yes
        Assert.Contains(@"Minimal\" + BootHardening.SafeBootName, h.SafeBoot); // היה מראש: לא מסירים
        Assert.DoesNotContain(@"Network\" + BootHardening.SafeBootName, h.SafeBoot);
    }

    [Fact]
    public void Release_without_prior_enforce_does_nothing()
    {
        var h = new FakeHost();
        BootHardening.Enforce(h, false);
        Assert.DoesNotContain(h.Commands, c => c.Contains("/set") || c.Contains("/disable") || c.Contains("/enable"));
    }

    [Fact]
    public void Failed_bcd_set_is_reported_incomplete_not_silent()
    {
        var h = new FakeHost { BcdSetFails = true };
        var r = BootHardening.Enforce(h, true);
        Assert.True(r.Incomplete);
        Assert.Contains(r.Steps, s => s.Id == "bcd.recoveryenabled" && !s.Ok);
        Assert.Contains("incomplete", h.State);
    }

    [Fact]
    public void Bcd_unreadable_leaves_everything_untouched_and_incomplete()
    {
        var h = new FakeHost();
        h.Bcd.Clear();
        // הפלט ללא identifier: מדמים כשל קריאה
        var h2 = new BrokenBcdHost();
        var r = BootHardening.Enforce(h2, true);
        Assert.Contains(r.Steps, s => s.Id == "bcd" && !s.Ok);
        Assert.DoesNotContain(h2.Commands, c => c.Contains("/set"));
    }

    private sealed class BrokenBcdHost : BootHardening.IBootHost
    {
        public List<string> Commands = new();
        public string Run(string f, string a) { Commands.Add(f + " " + a); return f == "bcdedit.exe" ? "Access is denied." : ""; }
        public bool SafeBootExists(string s, string n) => false;
        public bool SafeBootCreate(string s, string n) => true;
        public bool SafeBootDelete(string s, string n) => true;
        public string ReadState() => null;
        public void WriteState(string j) { }
        public void Log(string l) { }
    }

    [Fact]
    public void Enforce_twice_is_idempotent()
    {
        var h = new FakeHost();
        BootHardening.Enforce(h, true);
        int before = h.Commands.Count(c => c.Contains("/set") || c.Contains("/disable"));
        BootHardening.Enforce(h, true);
        Assert.Equal(before, h.Commands.Count(c => c.Contains("/set") || c.Contains("/disable")));
        BootHardening.Enforce(h, false);
        Assert.Equal("Yes", h.Bcd["recoveryenabled"]); // המקור המקורי נשמר גם אחרי אכיפה כפולה
    }

    [Fact]
    public void Findings_state_bitlocker_off_clearly()
    {
        var f = BootHardening.BuildFindings(new BootHardening.Posture { BitLockerProtection = 0, TpmPresent = true, TpmEnabled = true, SecureBoot = true });
        var bl = f.Single(x => x.Id == "bitlocker");
        Assert.Equal("bad", bl.Level);
        Assert.Contains("BitLocker כבוי", bl.Text);
    }

    [Fact]
    public void Findings_tpm_only_bitlocker_warns_and_pin_is_ok()
    {
        var warn = BootHardening.BuildFindings(new BootHardening.Posture { BitLockerProtection = 1, KeyProtectorTypes = new[] { 1, 3 } }).Single(x => x.Id == "bitlocker");
        Assert.Equal("warn", warn.Level);
        var ok = BootHardening.BuildFindings(new BootHardening.Posture { BitLockerProtection = 1, KeyProtectorTypes = new[] { 4, 3 } }).Single(x => x.Id == "bitlocker");
        Assert.Equal("ok", ok.Level);
    }

    [Fact]
    public void Findings_unknowns_are_not_reported_as_ok()
    {
        var f = BootHardening.BuildFindings(new BootHardening.Posture());
        Assert.Equal("unknown", f.Single(x => x.Id == "bitlocker").Level);
        Assert.Equal("unknown", f.Single(x => x.Id == "tpm").Level);
        Assert.Equal("warn", f.Single(x => x.Id == "secureBoot").Level);
        Assert.Equal("bad", BootHardening.BuildFindings(new BootHardening.Posture { SecureBoot = false }).Single(x => x.Id == "secureBoot").Level);
        Assert.Equal("bad", BootHardening.BuildFindings(new BootHardening.Posture { TpmPresent = true, TpmEnabled = false }).Single(x => x.Id == "tpm").Level);
    }
}
