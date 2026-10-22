using System.Diagnostics;
using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>בדיקות סבב r3: גשר לא-מהימן, pipe, backoff, מכסות, סביבה, mutex, צימוד, junction</summary>
public class ServiceHardeningR3Tests
{
    private const string Exe = @"C:\Program Files\OtzariaBridge\OtzariaBridge.exe";

    [Fact]
    public void Untrusted_bridges_are_real_path_bridges_the_service_did_not_launch()
    {
        var procs = new[] { new ProcInfo(1, 1, Exe), new ProcInfo(2, 1, Exe), new ProcInfo(3, 1, @"C:\Users\u\OtzariaBridge.exe"), new ProcInfo(4, 2, null) };
        var u = BridgeProcs.Untrusted(procs, Exe, new HashSet<int> { 1 });
        Assert.Single(u);
        Assert.Equal(2, u[0].Pid);
    }

    [Fact]
    public void Pipe_server_must_be_session0_and_service_path()
    {
        Assert.True(PipePeer.IsTrustedServer(Exe, 0, Exe));
        Assert.False(PipePeer.IsTrustedServer(Exe, 1, Exe));                       // תהליך משתמש
        Assert.False(PipePeer.IsTrustedServer(@"C:\Users\u\x.exe", 0, Exe));       // נתיב אחר
        Assert.True(PipePeer.IsTrustedServer(null, 0, Exe));                      // נתיב לא קריא אבל סשן 0 (לא משתמש) - לא חוסם קוד שחזור
    }

    [Fact]
    public void Backoff_grows_exponentially_and_is_capped()
    {
        var l = new AttemptLimiter(maxFailures: 3, lockSeconds: 10, maxLockSeconds: 60);
        long t = 1_000_000;
        for (int i = 0; i < 3; i++) l.Fail("a", t);
        Assert.True(l.IsLocked("a", t, out int s1)); Assert.Equal(10, s1);
        l.Fail("a", t); Assert.True(l.IsLocked("a", t, out int s2)); Assert.Equal(20, s2);
        l.Fail("a", t); l.Fail("a", t); l.Fail("a", t);
        Assert.True(l.IsLocked("a", t, out int s3)); Assert.Equal(60, s3);        // תקרה
    }

    [Fact]
    public void Correct_password_is_rate_limited_not_denied_during_backoff()
    {
        var l = new AttemptLimiter();
        long t = 5_000_000;
        for (int i = 0; i < 5; i++) l.Fail("u", t);
        Assert.False(l.CanTry("u", t + 100, out int wait));      // מיד אחרי: מוגבל קצב
        Assert.True(wait > 0);
        Assert.True(l.CanTry("u", t + 20_000, out _));           // ניסיון בדיקה מותר גם בתוך ההשהיה (סיסמה נכונה לא נחסמת ל-DoS)
        l.Fail("u", t + 20_000);                                 // S1: את חלון ה-probe צורך רק ניסיון שנכשל בפועל (CanTry לא משנה מצב)
        Assert.False(l.CanTry("u", t + 20_100, out _));          // אבל לא ברצף
        l.Success("u");
        Assert.True(l.CanTry("u", t + 20_200, out _));
        Assert.True(l.CanTry("other", t, out _));                // דלי נפרד
    }

    [Fact]
    public void ConnLimiter_per_sid_and_reserved_slots_for_elevated()
    {
        var c = new ConnLimiter(total: 10, perPid: 9, perSid: 3, reservedElevated: 4);
        Assert.True(c.TryEnter(1, "S-user")); Assert.True(c.TryEnter(2, "S-user")); Assert.True(c.TryEnter(3, "S-user"));
        Assert.False(c.TryEnter(4, "S-user"));                   // מכסה לפי SID
        for (int i = 10; i < 13; i++) Assert.True(c.TryEnter(i, "S-" + i));
        Assert.False(c.TryEnter(20, "S-x"));                     // 6 = 10-4 שמורים למורמים
        Assert.True(c.TryEnter(21, "S-admin", elevated: true));  // מורם נכנס למקום השמור
        c.Leave(21, "S-admin"); c.Leave(1, "S-user"); c.Leave(10, "S-10");
        Assert.True(c.TryEnter(5, "S-user"));
    }

    [Fact]
    public void Bundle_dir_forced_only_when_ready_and_replaces_existing()
    {
        var src = new List<string> { "PATH=x", "DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:/Users/u/evil" };
        Assert.Equal(src, EnvBlock.WithBundleDir(src, @"C:\ProgramData\OtzariaBridge\net", false));
        var f = EnvBlock.WithBundleDir(EnvBlock.Filter(src), @"C:\ProgramData\OtzariaBridge\net", true);
        Assert.Contains(@"DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:\ProgramData\OtzariaBridge\net", f);
        Assert.DoesNotContain(f, e => e.Contains("evil"));
    }

    [Fact]
    public void Held_mutex_does_not_stop_service_launched_bridge()
    {
        Assert.False(Program.ShouldExitOnMutex(true, false));
        Assert.True(Program.ShouldExitOnMutex(false, false));    // מופע כפול של משתמש
        Assert.False(Program.ShouldExitOnMutex(false, true));    // mutex squat מול גשר השירות
    }

    [Fact]
    public void Pairing_proof_is_hmac_of_nonce_keyed_by_token_hash()
    {
        string token = new string('a', 64);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        string nonce = "0123456789abcdef0123";
        string p1 = Pairing.ProofFor(hash, nonce);
        Assert.Equal(p1, Pairing.ProofFor(hash.ToLowerInvariant(), nonce));         // התוסף יכול לשלוח hex באותיות קטנות
        Assert.NotEqual(p1, Pairing.ProofFor(hash, nonce + "x"));
        Assert.NotEqual(p1, Pairing.ProofFor(new string('0', 64), nonce));           // גשר מזויף בלי הגיבוב
        Assert.Empty(Pairing.Proofs("short"));                                       // nonce קצר נדחה
    }

    [Fact]
    public void Token_default_dacl_is_hardened_sddl_and_off_by_default()
    {
        Assert.False(ProcessDacl.UseTokenDefaultDacl);
        Assert.DoesNotContain("GA;;;AU", ProcessDacl.TokenDefaultSddl());
    }

    [Fact]
    public void Temp_unlock_secret_min_length_is_8()
    {
        Assert.Equal(8, TempUnlock.MinSecretLength);
    }

    [Fact]
    public void Reparse_point_directory_is_detected()
    {
        string root = Path.Combine(Path.GetTempPath(), "r3junc-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "target"), link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            Assert.True(HardLock.IsReparse(link));
            Assert.False(HardLock.IsReparse(target));
            Assert.False(HardLock.IsReparse(Path.Combine(root, "missing")));
        }
        finally { try { if (Directory.Exists(link)) Directory.Delete(link, false); Directory.Delete(root, true); } catch { } }
    }
}
