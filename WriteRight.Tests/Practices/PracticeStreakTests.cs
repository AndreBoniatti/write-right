using WriteRight.Shared.Practices;

namespace WriteRight.Tests.Practices;

/// <summary>
/// A série de dias da barra de cima. Errar aqui não quebra nada visível: a chama
/// só passa a dizer que você quebrou a série num dia em que praticou — que é
/// exatamente o tipo de injustiça que faz alguém parar de olhar pra ela.
/// </summary>
public class PracticeStreakTests
{
    /// <summary>Fuso fixo (UTC−3, sem horário de verão), pra não depender do da máquina.</summary>
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("WR-03", TimeSpan.FromHours(-3), "UTC-3", "UTC-3");

    /// <summary>Um instante no relógio de quem está vendo.</summary>
    private static DateTimeOffset At(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(-3));

    private static StreakView Compute(DateTimeOffset now, params DateTimeOffset[] completedAt) =>
        PracticeStreak.Compute(completedAt, now, Zone);

    [Fact]
    public void Practicing_today_lights_the_streak_and_counts_today()
    {
        var view = Compute(At(10, 14, 23), At(10, 12, 21), At(10, 13, 22), At(10, 14, 21));

        Assert.Equal(StreakStatus.Lit, view.Status);
        Assert.Equal(3, view.Current);
    }

    [Fact]
    public void Before_practicing_today_the_streak_still_counts_up_to_yesterday()
    {
        var view = Compute(At(10, 14, 19), At(10, 12, 21), At(10, 13, 22));

        Assert.Equal(StreakStatus.AtRisk, view.Status);
        Assert.Equal(2, view.Current);
    }

    [Fact]
    public void A_missed_day_breaks_the_streak_and_keeps_the_last_run()
    {
        var view = Compute(At(10, 5, 19), At(9, 12, 22), At(9, 14, 22));

        Assert.Equal(StreakStatus.Off, view.Status);
        Assert.Equal(0, view.Current);
        Assert.Equal(1, view.LastRunLength);
        Assert.Equal(new DateOnly(2026, 9, 14), view.LastRunEnd);
    }

    [Fact]
    public void A_live_streak_carries_no_last_run()
    {
        var view = Compute(At(10, 14, 19), At(10, 13, 22));

        Assert.Null(view.LastRunLength);
        Assert.Null(view.LastRunEnd);
    }

    /// <summary>
    /// A razão da virada às 4h: a prática que começou às 23h40 e terminou às 00h30
    /// é do dia em que começou. Com a virada à meia-noite, ela pularia um dia e
    /// quebraria a série.
    /// </summary>
    [Fact]
    public void A_practice_finished_after_midnight_counts_for_the_day_before()
    {
        var view = Compute(At(10, 14, 21), At(10, 12, 23), At(10, 14, 0, 30));

        Assert.Equal(StreakStatus.AtRisk, view.Status); // a das 00h30 foi do dia 13
        Assert.Equal(2, view.Current);
    }

    [Fact]
    public void The_new_day_begins_at_4am()
    {
        Assert.Equal(new DateOnly(2026, 10, 13), PracticeStreak.DayOf(At(10, 14, 3, 59), Zone));
        Assert.Equal(new DateOnly(2026, 10, 14), PracticeStreak.DayOf(At(10, 14, 4, 0), Zone));
    }

    [Fact]
    public void Before_4am_today_is_still_the_day_before()
    {
        var view = Compute(At(10, 15, 2), At(10, 14, 21));

        Assert.Equal(StreakStatus.Lit, view.Status);
        Assert.Equal(new DateOnly(2026, 10, 14), view.Today);
    }

    /// <summary>
    /// O banco guarda UTC. 00h30 UTC do dia 15 são 21h30 do dia 14 aqui — contar
    /// pelo dia UTC poria quase toda prática noturna no dia seguinte.
    /// </summary>
    [Fact]
    public void The_day_is_the_one_on_the_viewers_clock_not_UTC()
    {
        var completed = new DateTimeOffset(2026, 10, 15, 0, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.Zero);

        var view = Compute(now, completed);

        Assert.Equal(new DateOnly(2026, 10, 14), view.Today);
        Assert.Equal(StreakStatus.Lit, view.Status);
    }

    [Fact]
    public void Several_practices_on_the_same_day_count_as_one_day()
    {
        var view = Compute(At(10, 14, 23), At(10, 14, 20), At(10, 14, 21), At(10, 14, 22));

        Assert.Equal(1, view.Current);
        Assert.Equal(1, view.Record);
    }

    [Fact]
    public void Record_is_the_longest_run_even_when_it_is_in_the_past()
    {
        var view = Compute(At(10, 14, 23),
            At(7, 24, 22), At(7, 25, 22), At(7, 26, 22), At(7, 27, 22),
            At(10, 13, 22), At(10, 14, 21));

        Assert.Equal(2, view.Current);
        Assert.Equal(4, view.Record);
    }

    [Fact]
    public void Recent_days_are_the_last_seven_ending_today()
    {
        var view = Compute(At(10, 14, 19), At(10, 9, 22), At(10, 12, 22));

        Assert.Equal(PracticeStreak.RecentDayCount, view.RecentDays.Count);
        Assert.Equal(new DateOnly(2026, 10, 8), view.RecentDays[0].Date);
        Assert.Equal(new DateOnly(2026, 10, 14), view.RecentDays[^1].Date);
        Assert.Equal([false, true, false, false, true, false, false], view.RecentDays.Select(d => d.Practiced));
    }

    [Fact]
    public void An_empty_history_is_off_with_no_record_and_no_last_run()
    {
        var view = Compute(At(10, 14, 19));

        Assert.Equal(StreakStatus.Off, view.Status);
        Assert.Equal(0, view.Current);
        Assert.Equal(0, view.Record);
        Assert.Null(view.LastRunLength);
        Assert.All(view.RecentDays, d => Assert.False(d.Practiced));
    }

    [Fact]
    public void A_completion_on_a_day_after_today_does_not_count()
    {
        var view = Compute(At(10, 14, 19), At(10, 15, 21));

        Assert.Equal(StreakStatus.Off, view.Status);
        Assert.Equal(0, view.Record);
    }
}
