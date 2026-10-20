using WriteRight.Shared.Profile;

namespace WriteRight.Tests.Profile;

/// <summary>
/// A régua do gráfico de evolução. Se ela escorregar, o gráfico continua bonito e
/// passa a contar outra história — exatamente o erro que a troca de "por prática"
/// pra "por palavra" veio corrigir.
/// </summary>
public class ErrorRateTests
{
    private static PracticePoint Point(int words, int errors, int id = 0) =>
        new(id, DateTimeOffset.UnixEpoch.AddDays(id), null, words, new SeverityCounts(0, errors, 0));

    /// <summary>Práticas 1..n, cada uma com 10 palavras e <c>errors(i)</c> erros.</summary>
    private static IReadOnlyList<PracticePoint> Series(int n, Func<int, int> errors) =>
        Enumerable.Range(1, n).Select(i => Point(10, errors(i), i)).ToList();

    // ── Palavras ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_text_has_no_words() => Assert.Equal(0, ErrorRate.Words("  \n "));

    [Fact]
    public void Words_are_split_by_any_whitespace_run() =>
        Assert.Equal(5, ErrorRate.Words("  I went\tto  the\nbeach. "));

    [Fact]
    public void Punctuation_and_hyphens_stay_inside_the_word() =>
        Assert.Equal(3, ErrorRate.Words("a well-known, place"));

    // ── Taxa ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_to_measure_has_no_rate()
    {
        Assert.Null(ErrorRate.Of([]));
        Assert.Null(ErrorRate.Of([Point(words: 0, errors: 2)]));
    }

    [Fact]
    public void Counts_every_severity_per_ten_words()
    {
        var point = new PracticePoint(1, DateTimeOffset.UnixEpoch, null, 20, new SeverityCounts(1, 2, 1));

        Assert.Equal(2.0, ErrorRate.Of([point]));
    }

    /// <summary>
    /// Soma ÷ soma: o texto de 30 palavras pesa três vezes o de 10. A média das taxas
    /// (1,0 e 2,0) daria 1,5 e deixaria o texto curto mandar no resultado.
    /// </summary>
    [Fact]
    public void Longer_texts_weigh_more_than_short_ones() =>
        Assert.Equal(1.75, ErrorRate.Of([Point(10, 1), Point(30, 6)]));

    // ── Média móvel ──────────────────────────────────────────────────────────

    [Fact]
    public void The_average_starts_at_the_minimum_number_of_practices()
    {
        var rolling = ErrorRate.Rolling(Series(ErrorRate.MinimumForAverage, i => i));

        Assert.All(rolling.Take(ErrorRate.MinimumForAverage - 1), Assert.Null);
        // 1+2+3+4+5 erros em 50 palavras.
        Assert.Equal(3.0, rolling[^1]);
    }

    [Fact]
    public void The_average_only_looks_at_the_last_window()
    {
        // Doze práticas: as duas primeiras com 10 erros, as outras sem erro nenhum.
        var rolling = ErrorRate.Rolling(Series(12, i => i <= 2 ? 10 : 0));

        Assert.Equal(12, rolling.Count);
        Assert.Equal(2.0, rolling[9]); // práticas 1..10: 20 erros em 100 palavras
        Assert.Equal(1.0, rolling[10]); // 2..11: a primeira saiu
        Assert.Equal(0.0, rolling[11]); // 3..12: as duas saíram
    }
}
