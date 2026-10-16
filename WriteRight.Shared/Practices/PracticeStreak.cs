using System;
using System.Collections.Generic;
using System.Linq;

namespace WriteRight.Shared.Practices;

/// <summary>Em que pé a série está agora — o que decide a cara da chama.</summary>
public enum StreakStatus
{
    /// <summary>Já praticou hoje: a série inclui o dia de hoje.</summary>
    Lit,

    /// <summary>Ainda não praticou hoje, mas ontem sim: a série vale até o dia virar.</summary>
    AtRisk,

    /// <summary>Nem hoje nem ontem: não há série.</summary>
    Off,
}

/// <summary>Um dia da faixa de dias recentes: a data e se houve prática nela.</summary>
public sealed record StreakDay(DateOnly Date, bool Practiced);

/// <summary>
/// A série de dias com prática concluída, vista de um instante.
/// <see cref="LastRunLength"/>/<see cref="LastRunEnd"/> só vêm com a série zerada:
/// é o que impede uma série quebrada de ser só um 0 na tela.
/// </summary>
public sealed record StreakView(
    StreakStatus Status,
    int Current,
    int Record,
    DateOnly Today,
    IReadOnlyList<StreakDay> RecentDays,
    int? LastRunLength,
    DateOnly? LastRunEnd);

/// <summary>
/// Calcula a série de dias seguidos com prática concluída. Pura: recebe os instantes
/// de conclusão, o agora e o fuso — o servidor não sabe o fuso de quem está vendo,
/// e "dia" só faz sentido no relógio dessa pessoa.
/// </summary>
public static class PracticeStreak
{
    /// <summary>
    /// O dia vira às 4h, não à meia-noite. A prática costuma ser à noite e às vezes
    /// passa da meia-noite; com a virada às 0h, terminar às 00h30 a prática que
    /// começou às 23h40 contava pro dia seguinte, e a série quebrava num dia em que
    /// houve prática. No histórico real, isso derrubava a maior série de 9 pra 6.
    /// </summary>
    public static readonly TimeSpan DayStartsAt = TimeSpan.FromHours(4);

    /// <summary>Quantos dias a faixa de dias recentes mostra, terminando hoje.</summary>
    public const int RecentDayCount = 7;

    /// <summary>O dia de prática a que um instante pertence, no fuso dado.</summary>
    public static DateOnly DayOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime - DayStartsAt);

    public static StreakView Compute(
        IEnumerable<DateTimeOffset> completedAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DayOf(now, zone);

        // Conclusão "no futuro" só aparece com relógio fora de hora; contá-la faria a
        // série valer um dia que ainda não aconteceu.
        var days = completedAt.Select(t => DayOf(t, zone)).Where(d => d <= today).ToHashSet();

        var practicedToday = days.Contains(today);
        var current = RunEndingAt(days, practicedToday ? today : today.AddDays(-1));
        var status = practicedToday ? StreakStatus.Lit
            : current > 0 ? StreakStatus.AtRisk
            : StreakStatus.Off;

        int? lastRunLength = null;
        DateOnly? lastRunEnd = null;
        if (status == StreakStatus.Off && days.Count > 0)
        {
            var end = days.Max();
            lastRunEnd = end;
            lastRunLength = RunEndingAt(days, end);
        }

        var recent = Enumerable.Range(0, RecentDayCount)
            .Select(i => today.AddDays(i - (RecentDayCount - 1)))
            .Select(d => new StreakDay(d, days.Contains(d)))
            .ToList();

        return new StreakView(status, current, Record(days), today, recent, lastRunLength, lastRunEnd);
    }

    /// <summary>Quantos dias seguidos com prática terminam em <paramref name="end"/>.</summary>
    private static int RunEndingAt(HashSet<DateOnly> days, DateOnly end)
    {
        var length = 0;
        for (var d = end; days.Contains(d); d = d.AddDays(-1)) length++;
        return length;
    }

    /// <summary>A maior série do histórico, a atual inclusive.</summary>
    private static int Record(HashSet<DateOnly> days)
    {
        int record = 0, run = 0;
        DateOnly? previous = null;
        foreach (var d in days.OrderBy(d => d))
        {
            run = previous is { } p && d == p.AddDays(1) ? run + 1 : 1;
            record = Math.Max(record, run);
            previous = d;
        }
        return record;
    }
}
