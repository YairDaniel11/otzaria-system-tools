using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>זהות אוצריא: מיקום מהימן / נעיצת SHA256 לפני הפעלה, והשוואת נתיבים</summary>
public class OtzariaIdentityTests
{
    [Fact]
    public void Trusted_location_always_allowed() =>
        Assert.Equal(Kiosk.LaunchVerdict.Allow, Kiosk.DecideLaunch(true, "AA", "BB"));

    [Fact]
    public void Writable_location_with_matching_pin_allowed() =>
        Assert.Equal(Kiosk.LaunchVerdict.Allow, Kiosk.DecideLaunch(false, "abc", "ABC"));

    [Fact]
    public void Writable_location_with_changed_file_refused() =>
        Assert.Equal(Kiosk.LaunchVerdict.Refuse, Kiosk.DecideLaunch(false, "abc", "def"));

    [Fact]
    public void Writable_location_without_pin_pins_once() =>
        Assert.Equal(Kiosk.LaunchVerdict.AllowAndPin, Kiosk.DecideLaunch(false, null, "def"));

    [Fact]
    public void Unreadable_file_in_writable_location_refused() =>
        Assert.Equal(Kiosk.LaunchVerdict.Refuse, Kiosk.DecideLaunch(false, "abc", null));

    [Fact]
    public void SamePath_compares_full_path_case_insensitively()
    {
        Assert.True(Kiosk.SamePath(@"C:\A\b\..\otzaria.exe", @"c:\a\OTZARIA.exe"));
        Assert.False(Kiosk.SamePath(@"C:\A\otzaria.exe", @"C:\B\otzaria.exe"));
        Assert.False(Kiosk.SamePath(null, @"C:\A\otzaria.exe"));
    }
}
