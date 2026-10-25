using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>
/// [x2] מקש-שינוי תקוע, סימון הזרקה, ומדיניות נגישות. WHY: Win תקוע בלע כל מקש וגם את ה-up שלו => מקלדת מתה בנעילה.
/// </summary>
public class X2Tests
{
    [Fact] public void Win_stuck_on_first_typed_key()
    {
        var w = StuckModifier.Win();
        Assert.Equal(StuckVerdict.Stuck, w.Track(true, false, true, 1000));
    }

    [Fact] public void Win_not_down_never_stuck() =>
        Assert.Equal(StuckVerdict.NotDown, StuckModifier.Win().Track(false, false, true, 1000));

    [Fact] public void Shift_needs_three_keys_and_five_seconds()
    {
        var s = StuckModifier.Shift();
        Assert.Equal(StuckVerdict.Held, s.Track(true, false, true, 0));       // ראשון: מתחיל
        Assert.Equal(StuckVerdict.Held, s.Track(true, false, true, 1000));
        Assert.Equal(StuckVerdict.Held, s.Track(true, false, true, 2000));    // 3 מקשים אבל רק 2 שניות
        Assert.Equal(StuckVerdict.Stuck, s.Track(true, false, true, 5500));
    }

    [Fact] public void Held_shift_with_autorepeat_is_never_cut_off()
    {
        var s = StuckModifier.Shift();
        for (long t = 0; t < 30_000; t += 300)
        {
            Assert.Equal(StuckVerdict.Held, s.Track(true, true, false, t));   // auto-repeat של Shift עצמו מאפס
            Assert.Equal(StuckVerdict.Held, s.Track(true, false, true, t + 100));
        }
    }

    [Fact] public void Release_resets_state()
    {
        var c = StuckModifier.Ctrl();
        c.Track(true, false, true, 0); c.Track(true, false, true, 100);
        Assert.Equal(StuckVerdict.NotDown, c.Track(false, false, true, 200));
        Assert.Equal(StuckVerdict.Held, c.Track(true, false, true, 6000));    // סופר מחדש
    }

    [Fact] public void Injected_release_passes_only_when_marked_armed_injected_and_modifier_up()
    {
        var m = StuckInput.Magic; uint inj = StuckInput.LlkhfInjected;
        Assert.True(StuckInput.IsPassable(m, inj, 0x5B, false, 2000, 1000));                  // Win up
        Assert.True(StuckInput.IsPassable(m, inj, StuckInput.VkNoName, true, 2000, 1000));    // מקש "כלום"
        Assert.False(StuckInput.IsPassable(m, inj, 0x5B, true, 2000, 1000), "Win down עם Magic חייב להיחסם");
        Assert.False(StuckInput.IsPassable(m, inj, 0x52, true, 2000, 1000), "R down עם Magic (Win+R) חייב להיחסם");
        Assert.False(StuckInput.IsPassable(m, inj, 0x52, false, 2000, 1000), "אות עם Magic לא עוברת");
        Assert.False(StuckInput.IsPassable(m, inj, 0x5B, false, 2000, 3000), "מחוץ לחלון ה-arm");
        Assert.False(StuckInput.IsPassable(m, 0, 0x5B, false, 2000, 1000), "לא מוזרק");
        Assert.False(StuckInput.IsPassable(IntPtr.Zero, inj, 0x5B, false, 2000, 1000), "בלי Magic");
    }

    [Fact] public void Win_and_Alt_get_noname_key_before_release_but_ctrl_does_not()
    {
        var win = StuckInput.BuildRelease(true, new byte[] { 0x5B });
        Assert.Equal(new[] { (StuckInput.VkNoName, false), (StuckInput.VkNoName, true), ((byte)0x5B, true) }, win);
        Assert.Equal(new[] { ((byte)0xA2, true) }, StuckInput.BuildRelease(false, new byte[] { 0xA2 }));
        Assert.Empty(StuckInput.BuildRelease(true, Array.Empty<byte>()));
        Assert.True(StuckInput.NeedsNoName(StuckModifier.Win()));
        Assert.True(StuckInput.NeedsNoName(StuckModifier.Alt()));
        Assert.False(StuckInput.NeedsNoName(StuckModifier.Ctrl()));
    }

    [Fact] public void Idle_stuck_thresholds()
    {
        Assert.True(StuckInput.IdleStuck(true, true, 1000, 1000 + StuckInput.IdleWinMs));
        Assert.False(StuckInput.IdleStuck(true, true, 1000, 1000 + StuckInput.IdleWinMs - 1));
        Assert.False(StuckInput.IdleStuck(true, false, 1000, 1000 + StuckInput.IdleWinMs));      // Ctrl/Alt/Shift: סף ארוך יותר
        Assert.True(StuckInput.IdleStuck(true, false, 1000, 1000 + StuckInput.IdleModMs));
        Assert.False(StuckInput.IdleStuck(false, true, 1000, 999_999));                          // לא לחוץ
        Assert.False(StuckInput.IdleStuck(true, true, 0, 999_999));                              // אין אירוע מתועד: לא שופטים
    }

    // Win נשאר חסום ושחזור Ctrl+Alt+O לא נפגע
    [Fact] public void Win_still_blocked_and_return_hotkey_intact()
    {
        var c = new KioskKeys.Ctx(false, false, false, false, false, false, false, false);
        Assert.True(KioskKeys.ShouldBlock(KioskKeys.VkLWin, c));
        Assert.True(KioskKeys.ShouldBlock(0x45, c with { Win = true }));
        Assert.True(KioskKeys.IsReturnHotkey(KioskKeys.VkO, true, true, true, true, false));
    }

    [Fact] public void Accessibility_hotkey_bit_only()
    {
        Assert.Equal(0x1u | 0x8u, AccessibilityPolicy.WithoutHotkey(0x1 | 0x4 | 0x8));
        Assert.True(AccessibilityPolicy.NeedsDisable(0x4));
        Assert.False(AccessibilityPolicy.NeedsDisable(0x1));
        Assert.Equal((uint?)(0x1 | 0x4), AccessibilityPolicy.Restored(0x1, true));
        Assert.Null(AccessibilityPolicy.Restored(0x1 | 0x4, true));    // כבר דלוק: לא נוגעים
        Assert.Null(AccessibilityPolicy.Restored(0x1, false));          // לא היה דלוק במקור: לא מדליקים
    }

    [Fact] public void Policy_restore_only_if_still_ours()
    {
        Assert.Equal((true, (int?)null), AccessibilityPolicy.PolicyRestore(null, 1, 1));   // לא היה קיים => מוחקים
        Assert.Equal((true, (int?)5), AccessibilityPolicy.PolicyRestore(5, 255, 255));     // היה => מחזירים
        Assert.Equal((false, (int?)null), AccessibilityPolicy.PolicyRestore(null, 7, 1));  // מישהו שינה אחרינו => לא נוגעים
        Assert.Contains(AccessibilityPolicy.PolicyValues, p => p.Name == "NoDriveTypeAutoRun" && p.Value == 0xFF);
        Assert.Contains(AccessibilityPolicy.PolicyValues, p => p.Name == "NoWinKeys" && p.Value == 1);
    }
}
