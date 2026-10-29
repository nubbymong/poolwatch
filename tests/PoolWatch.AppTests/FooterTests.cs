using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The main window's footer: the count beside Active changes (shown only once it is read, said in words to a screen
/// reader) and when it is read again. The controls are never shown.
/// </summary>
public class FooterTests
{
    [Fact]
    public void TheCountShowsOnlyOnceReadAndSaysItselfInWords()
    {
        using var badge = new CountBadge();
        Assert.False(badge.Visible); // not read yet: no number, not even 0
        Assert.Null(badge.Count);

        badge.Count = 1;
        Assert.True(badge.Visible);
        Assert.Equal(("1", "1 active change"), (badge.Text, badge.AccessibleName));
        int one = badge.Width;

        badge.Count = 12;
        Assert.Equal(("12", "12 active changes"), (badge.Text, badge.AccessibleName));
        Assert.True(badge.Width > one, $"12 is {badge.Width} px wide, 1 is {one}");
        Assert.Equal(badge.GetPreferredSize(Size.Empty), badge.Size);

        badge.Count = 0;
        Assert.True(badge.Visible); // nothing in effect is a count too
        Assert.Equal("0 active changes", badge.AccessibleName);

        badge.Count = null;
        Assert.False(badge.Visible);
        Assert.Equal("", badge.AccessibleName);
    }

    [Fact]
    public void TheCountIsReadWhenTheWindowShowsWhenItComesBackAfterAChangeAndEveryFiveMinutes()
    {
        var t0 = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
        var s = new ChangeCountSchedule();
        Assert.True(s.Start(t0));                                     // never read: read now
        Assert.False(s.Start(t0.AddMinutes(6)));                      // one read at a time
        s.Done();
        Assert.False(s.Start(t0.AddMinutes(4)));                      // read 4 minutes ago
        Assert.True(s.Start(t0.AddMinutes(5)));                       // every 5 minutes
        s.Done();

        var t1 = t0.AddMinutes(5);
        s.Activated(t1.AddSeconds(20));                               // back to the front at once: the read is fresh
        Assert.False(s.Start(t1.AddSeconds(20)));
        s.Activated(t1.AddSeconds(40));                               // back from Active changes later: read again
        Assert.True(s.Start(t1.AddSeconds(40)));
        s.Done();

        s.Soon();                                                     // a card made a change, or the window showed again
        Assert.True(s.Start(t1.AddSeconds(41)));
        Assert.False(s.Start(t1.AddSeconds(42)));
        s.Done();
        Assert.Equal((TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30)), (ChangeCountSchedule.Every, ChangeCountSchedule.AgainAfter));
    }
}
