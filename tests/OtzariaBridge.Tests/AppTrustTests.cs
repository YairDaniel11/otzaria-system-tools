using System.Security.AccessControl;
using Microsoft.Win32;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>בדיקות ללוגיקת זהות/אמון (AppTrust, Programs): התאמה לפי נתיב+נעיצה, ACL, זהות חסומה, PID reuse ותוקף</summary>
public class AppTrustTests
{
    private sealed class FakeEnv : AppTrust.ITrustEnv
    {
        public HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AppTrust.SigInfo> Sigs = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Shas = new(StringComparer.OrdinalIgnoreCase);
        public bool IsTrustedLocation(string p) => Trusted.Contains(p);
        public AppTrust.SigInfo Sig(string p) => Sigs.TryGetValue(p, out var s) ? s : AppTrust.SigInfo.None;
        public string Sha256(string p) => Shas.TryGetValue(p, out var s) ? s : "";
    }

    private const string Exe = @"D:\Apps\zayit\zayit.exe";
    private static readonly string[] Names = { "zayit" };

    [Fact]
    public void Exact_path_with_matching_hash_matches()
    {
        var env = new FakeEnv(); env.Shas[Exe] = "aa";
        Assert.Equal(AppTrust.MatchKind.Exact, AppTrust.MatchExe(Exe, Exe, null, Names, "aa", "", env));
    }

    [Fact]
    public void Exact_path_replaced_file_does_not_match()
    {
        var env = new FakeEnv(); env.Shas[Exe] = "bb";
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(Exe, Exe, null, Names, "aa", "", env));
    }

    [Fact]
    public void Signer_pin_survives_update_but_other_publisher_fails()
    {
        var env = new FakeEnv(); env.Shas[Exe] = "new";
        env.Sigs[Exe] = new AppTrust.SigInfo(true, "CN=Good");
        Assert.Equal(AppTrust.MatchKind.Exact, AppTrust.MatchExe(Exe, Exe, null, Names, "old", "CN=Good", env));
        env.Sigs[Exe] = new AppTrust.SigInfo(true, "CN=Evil");
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(Exe, Exe, null, Names, "old", "CN=Good", env));
        env.Sigs[Exe] = new AppTrust.SigInfo(false, "CN=Good");   // חתימה לא תקפה
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(Exe, Exe, null, Names, "old", "CN=Good", env));
    }

    [Fact]
    public void Unpinned_entry_matches_only_in_trusted_location()
    {
        var env = new FakeEnv();
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(Exe, Exe, null, Names, "", "", env));
        env.Trusted.Add(Exe);
        Assert.Equal(AppTrust.MatchKind.Exact, AppTrust.MatchExe(Exe, Exe, null, Names, "", "", env));
    }

    [Fact]
    public void Process_name_alone_is_not_enough()
    {
        var env = new FakeEnv();
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(@"C:\Users\u\Downloads\zayit.exe", Exe, null, Names, "aa", "CN=Good", env));
    }

    [Fact]
    public void Any_exe_in_install_dir_requires_verification()
    {
        var env = new FakeEnv();
        string helper = @"D:\Apps\zayit\evil.exe";
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(helper, Exe, @"D:\Apps\zayit", Names, "aa", "CN=Good", env));
        env.Sigs[helper] = new AppTrust.SigInfo(true, "CN=Good");
        // [s2] חתימה בלבד (בלי מיקום מנהל-בלבד) כבר לא מספיקה ל-Helper
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(helper, Exe, @"D:\Apps\zayit", Names, "aa", "CN=Good", env));
        env.Sigs[helper] = AppTrust.SigInfo.None; env.Trusted.Add(helper);
        Assert.Equal(AppTrust.MatchKind.Helper, AppTrust.MatchExe(helper, Exe, @"D:\Apps\zayit", Names, "aa", "CN=Good", env));
    }

    [Fact]
    public void Missing_exe_path_fails_closed()
        => Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(null, Exe, null, Names, "aa", "", new FakeEnv()));

    [Fact]
    public void LaunchAllowed_rejects_swapped_file()
    {
        var env = new FakeEnv(); env.Shas[Exe] = "swapped";
        Assert.False(AppTrust.LaunchAllowed(Exe, "aa", "", env));
        env.Shas[Exe] = "aa";
        Assert.True(AppTrust.LaunchAllowed(Exe, "aa", "", env));
    }

    // ---- זהות חסומה ----
    private static readonly HashSet<string> Blk = new(StringComparer.OrdinalIgnoreCase) { "cmd", "powershell", "certutil" };

    [Fact]
    public void Renamed_cmd_is_caught_by_original_filename()
        => Assert.True(AppTrust.IsBlockedIdentity("calc", @"C:\Temp\calc.exe", "Cmd.Exe", Blk));

    [Fact]
    public void Blocked_by_name_or_file_stem_and_clean_process_passes()
    {
        Assert.True(AppTrust.IsBlockedIdentity("CMD", null, null, Blk));
        Assert.True(AppTrust.IsBlockedIdentity("x", @"C:\Windows\System32\certutil.exe", "", Blk));
        Assert.False(AppTrust.IsBlockedIdentity("WINWORD", @"C:\Program Files\Office\WINWORD.EXE", "WinWord.exe", Blk));
    }

    [Fact]
    public void Expanded_blocked_list_covers_required_tools()
    {
        foreach (var n in new[] { "wmic", "forfiles", "pcalua", "msbuild", "csc", "installutil", "regsvr32", "certutil", "bitsadmin", "dism",
                 "schtasks", "sc", "net", "reg", "curl", "ftp", "mshta", "wscript", "cscript", "rundll32", "regedit", "mmc", "control",
                 "eventvwr", "mstsc", "SystemSettings", "ApplicationFrameHost", "osk", "narrator", "magnify", "utilman", "notepad", "mspaint" })
            Assert.True(Programs.IsBlockedName(n), n);
    }

    // ---- ACL ----
    private static AppTrust.AclRule R(string sid, FileSystemRights r, bool inheritOnly = false) => new(sid, r, true, inheritOnly);
    private const string Sys = "S-1-5-18", Admins = "S-1-5-32-544", Users = "S-1-5-32-545", Auth = "S-1-5-11", CreatorOwner = "S-1-3-0";

    [Fact]
    public void ProgramFiles_like_acl_is_admin_only()
    {
        var rules = new[]
        {
            R(Sys, FileSystemRights.FullControl), R(Admins, FileSystemRights.FullControl),
            R(Users, FileSystemRights.ReadAndExecute), R(CreatorOwner, FileSystemRights.FullControl, inheritOnly: true),
        };
        Assert.True(AppTrust.IsAdminOnlyWritable(rules, Admins));
    }

    [Theory]
    [InlineData(Users, FileSystemRights.Modify)]
    [InlineData(Auth, FileSystemRights.WriteData)]
    [InlineData("S-1-1-0", FileSystemRights.FullControl)]
    [InlineData(Users, FileSystemRights.AppendData)]
    [InlineData(Users, FileSystemRights.Delete)]
    public void User_writable_acl_is_rejected(string sid, FileSystemRights rights)
        => Assert.False(AppTrust.IsAdminOnlyWritable(new[] { R(Admins, FileSystemRights.FullControl), R(sid, rights) }, Admins));

    [Fact]
    public void Non_admin_owner_is_rejected_and_unknown_sid_is_writer()
    {
        Assert.False(AppTrust.IsAdminOnlyWritable(new[] { R(Admins, FileSystemRights.FullControl) }, "S-1-5-21-1-2-3-1001"));
        Assert.False(AppTrust.IsAdminOnlyWritable(new[] { R("S-1-5-21-1-2-3-1001", FileSystemRights.Write) }, Admins));
        Assert.False(AppTrust.IsAdminOnlyWritable(new[] { R(Admins, FileSystemRights.FullControl) }, null));
    }

    [Fact]
    public void Ancestor_may_allow_creating_children_but_not_deleting_them()
    {
        var root = new[] { R(Admins, FileSystemRights.FullControl), R(Auth, FileSystemRights.AppendData) };
        Assert.True(AppTrust.IsAdminOnlyWritable(root, Admins, ancestor: true));
        Assert.False(AppTrust.IsAdminOnlyWritable(root, Admins, ancestor: false));
        Assert.False(AppTrust.IsAdminOnlyWritable(new[] { R(Users, FileSystemRights.DeleteSubdirectoriesAndFiles) }, Admins, ancestor: true));
    }

    [Fact]
    public void Under_roots_rejects_sibling_prefix_unc_and_drive_root()
    {
        var roots = new[] { @"C:\Program Files", @"D:\" };
        Assert.True(AppTrust.IsUnderAnyRoot(@"C:\Program Files\X\a.exe", roots));
        Assert.False(AppTrust.IsUnderAnyRoot(@"C:\Program Files X\a.exe", roots));
        Assert.False(AppTrust.IsUnderAnyRoot(@"D:\a.exe", roots));
        Assert.False(AppTrust.IsUnderAnyRoot(@"\\srv\share\a.exe", new[] { @"\\srv\share" }));
        Assert.False(AppTrust.IsUnderAnyRoot(@"C:\Program Files\..\Users\u\a.exe", roots));
    }

    // ---- זיהוי אוטומטי: אין הזרקה דרך רישום משתמש/תיקיות משתמש ----
    [Fact]
    public void Auto_detect_does_not_trust_HKCU()
    {
        Assert.DoesNotContain(RegistryHive.CurrentUser, Programs.TrustedHives(true));
        Assert.Contains(RegistryHive.CurrentUser, Programs.TrustedHives(false));
    }

    [Fact]
    public void Auto_candidate_from_user_registry_path_is_rejected()
    {
        var env = new FakeEnv();
        Assert.False(Programs.AutoCandidateOk(@"C:\Users\u\Desktop\zayit.exe", env));
        Assert.False(Programs.AutoCandidateOk(@"E:\otzar.exe", env));
        string good = @"C:\Program Files\Zayit\zayit.exe"; env.Trusted.Add(good);
        Assert.True(Programs.AutoCandidateOk(good, env));
        string shell = @"C:\Program Files\X\cmd.exe"; env.Trusted.Add(shell);
        Assert.False(Programs.AutoCandidateOk(shell, env));
    }

    // ---- LaunchedSet: תוקף 10 דקות ו-PID reuse ----
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static AppTrust.ProcInfo Alive(DateTime s) => new(true, s);
    private static readonly AppTrust.ProcInfo Dead = new(false, null);

    [Fact]
    public void Launched_expires_after_ten_minutes()
    {
        var set = new AppTrust.LaunchedSet();
        set.Remember(100, T0, T0);
        Assert.True(set.Contains(100, Alive(T0), T0.AddMinutes(9.9)));
        // [s2] שורש חי נשאר מהימן כל חייו; התוקף נאכף רק על שורש שמת (ר' S2Tests)
        Assert.True(set.Contains(100, Alive(T0), T0.AddMinutes(10.1)));
        Assert.False(set.Contains(100, Dead, T0.AddMinutes(10.1), T0.AddMinutes(10)));
    }

    [Fact]
    public void Pid_reuse_with_different_creation_time_is_rejected()
    {
        var set = new AppTrust.LaunchedSet();
        set.Remember(100, T0, T0);
        Assert.False(set.Contains(100, Alive(T0.AddMinutes(3)), T0.AddMinutes(4)));
        Assert.False(set.Contains(100, new AppTrust.ProcInfo(true, null), T0.AddMinutes(4)));
    }

    [Fact]
    public void Tree_child_of_live_launched_process_is_accepted_and_reused_parent_rejected()
    {
        var set = new AppTrust.LaunchedSet();
        set.Remember(100, T0, T0);
        var procs = new Dictionary<int, AppTrust.ProcInfo> { [100] = Alive(T0), [200] = Alive(T0.AddSeconds(5)) };
        var parents = new Dictionary<int, int> { [200] = 100 };
        Assert.True(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddSeconds(30)));
        procs[100] = Alive(T0.AddMinutes(1));
        Assert.False(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddSeconds(90)));
    }

    [Fact]
    public void Tree_child_of_dead_launcher_accepted_only_if_born_after_launch_and_within_validity()
    {
        var set = new AppTrust.LaunchedSet();
        set.Remember(100, T0, T0);
        var procs = new Dictionary<int, AppTrust.ProcInfo> { [200] = Alive(T0.AddSeconds(5)) };
        var parents = new Dictionary<int, int> { [200] = 100 };
        Assert.True(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddMinutes(1)));
        Assert.False(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddMinutes(11)));
        procs[200] = Alive(T0.AddSeconds(-5));
        Assert.False(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddMinutes(1)));
    }
}
