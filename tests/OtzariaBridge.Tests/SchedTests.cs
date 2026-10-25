using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

public class SchedTests
{
    [Theory]
    [InlineData("20:00", true)]
    [InlineData("08:30", true)]
    [InlineData("0:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)] // מחוץ לטווח היום
    [InlineData("8:5", false)]   // דקות חייבות שתי ספרות
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseTime_validates_format(string input, bool expectValid)
    {
        Assert.Equal(expectValid, Sched.ParseTime(input) != null);
        Assert.Equal(expectValid, Sched.IsValid(input));
    }

    [Fact]
    public void InWindow_same_day_window()
    {
        var day = new DateTime(2026, 1, 1);
        Assert.False(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(8))); // לפני
        Assert.True(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(9)));  // בדיוק ההתחלה — כלול
        Assert.True(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(12))); // באמצע
        Assert.False(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(17))); // בדיוק הסוף — לא כלול
        Assert.False(Sched.InWindow("09:00", "17:00", day + TimeSpan.FromHours(18))); // אחרי
    }

    [Fact]
    public void InWindow_crosses_midnight()
    {
        var day = new DateTime(2026, 1, 1);
        // 20:00–02:00: נעול מ-20:00 בערב עד 02:00 בלילה שאחריו
        Assert.False(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(19)));          // לפני הערב
        Assert.True(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(20)));            // תחילת הערב
        Assert.True(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(23)));            // לפני חצות
        Assert.True(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(24).Add(TimeSpan.FromHours(1)))); // 01:00 שאחרי חצות
        Assert.False(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(24).Add(TimeSpan.FromHours(2)))); // 02:00 — סוף, לא כלול
        Assert.False(Sched.InWindow("20:00", "02:00", day + TimeSpan.FromHours(24).Add(TimeSpan.FromHours(10)))); // צהריים שלמחרת
    }

    [Fact]
    public void InWindow_equal_start_and_end_never_locks()
    {
        // קצה בטוח: לא רוצים שחלון "ריק" ייחשב כל היום
        Assert.False(Sched.InWindow("10:00", "10:00", new DateTime(2026, 1, 1, 10, 0, 0)));
        Assert.False(Sched.InWindow("10:00", "10:00", new DateTime(2026, 1, 1, 23, 59, 0)));
    }

    [Theory]
    [InlineData(null, "10:00")]
    [InlineData("10:00", null)]
    [InlineData("bad", "10:00")]
    [InlineData("10:00", "bad")]
    public void InWindow_invalid_input_never_locks(string start, string end)
    {
        Assert.False(Sched.InWindow(start, end, DateTime.Now));
    }

    [Fact]
    public void CurrentWindowEnd_single_window_same_day_and_across_midnight()
    {
        var day = new DateTime(2026, 1, 1);
        Assert.Null(Sched.CurrentWindowEnd("09:00", "17:00", day + TimeSpan.FromHours(8)));
        Assert.Equal(day + TimeSpan.FromHours(17), Sched.CurrentWindowEnd("09:00", "17:00", day + TimeSpan.FromHours(12)));
        // חלון שחוצה חצות: בערב הסוף מחר, אחרי חצות הסוף היום
        Assert.Equal(day.AddDays(1) + TimeSpan.FromHours(2), Sched.CurrentWindowEnd("20:00", "02:00", day + TimeSpan.FromHours(21)));
        Assert.Equal(day + TimeSpan.FromHours(2), Sched.CurrentWindowEnd("20:00", "02:00", day + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void CurrentWindowEnd_several_windows_returns_latest_end_of_those_containing_now()
    {
        var day = new DateTime(2026, 1, 1);
        var windows = new[] { ("09:00", "12:00"), ("11:00", "15:00"), ("18:00", "19:00") };
        Assert.Null(Sched.CurrentWindowEnd(windows, day + TimeSpan.FromHours(8)));
        Assert.Equal(day + TimeSpan.FromHours(12), Sched.CurrentWindowEnd(windows, day + TimeSpan.FromHours(10)));
        Assert.Equal(day + TimeSpan.FromHours(15), Sched.CurrentWindowEnd(windows, day + TimeSpan.FromHours(11.5))); // חופפים - המאוחר
    }

    [Fact]
    public void ParseWindows_skips_invalid_items()
    {
        var w = Sched.ParseWindows("20:00-23:00, bad, 5:30-6:30,25:00-01:00,,09:00");
        Assert.Equal(new[] { ("20:00", "23:00"), ("5:30", "6:30") }, w.ToArray());
        Assert.Empty(Sched.ParseWindows(null));
        Assert.Empty(Sched.ParseWindows(""));
    }
}
