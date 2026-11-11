using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>מצב תחזוקה: 30 דקות על שעון מונוטוני, כשל סגור (לא הופעל / עבר / ערך לא סביר = נעול)</summary>
public class MaintenanceTests
{
    [Fact]
    public void Duration_is_30_minutes_and_within_temp_unlock_max()
    {
        Assert.Equal(30, Maintenance.Minutes);
        Assert.True(Maintenance.Minutes <= TempUnlock.MaxMinutes);
        Assert.Equal(1_000_000 + 30 * 60_000L, Maintenance.EndTick(1_000_000));
    }

    [Fact]
    public void SecondsLeft_counts_down_then_expires()
    {
        long now = 5_000_000, end = Maintenance.EndTick(now);
        Assert.Equal(1800, Maintenance.SecondsLeft(end, now));
        Assert.Equal(1, Maintenance.SecondsLeft(end, end - 1));
        Assert.Equal(0, Maintenance.SecondsLeft(end, end));
        Assert.Equal(0, Maintenance.SecondsLeft(end, end + 10_000));
    }

    [Fact]
    public void SecondsLeft_fails_closed_for_unset_or_absurd_end()
    {
        Assert.Equal(0, Maintenance.SecondsLeft(0, 123));
        Assert.Equal(0, Maintenance.SecondsLeft(10_000_000 + 3 * 3_600_000L, 10_000_000)); // רחוק מדי = פג
    }

    [Fact]
    public void Not_active_by_default()
    {
        Assert.Equal(0, Kiosk.MaintenanceSecondsLeft());
        Assert.False(Kiosk.MaintenanceActive);
    }

    [Fact]
    public void Start_requires_locked_state()
    {
        // בלי נעילה פעילה אי אפשר לפתוח מצב תחזוקה (ואין שום מצב שנשאר)
        Assert.ThrowsAny<Exception>(() => Kiosk.StartMaintenance("x"));
        Assert.False(Kiosk.MaintenanceActive);
    }
}
