using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Profile;

/// <summary>
/// A contagem por gravidade que as barras e as colunas do perfil desenham. Se a ordem
/// escorregar, a pilha passa a pôr a lapidação na base — e a cor de "quebra o sentido"
/// passa a pintar o que é só detalhe.
/// </summary>
public class SeverityCountsTests
{
    [Fact]
    public void Counts_each_severity()
    {
        var counts = SeverityCounts.Of(
        [
            ErrorSeverity.Polish, ErrorSeverity.BreaksMeaning,
            ErrorSeverity.Understandable, ErrorSeverity.Understandable,
        ]);

        Assert.Equal(new SeverityCounts(BreaksMeaning: 1, Understandable: 2, Polish: 1), counts);
        Assert.Equal(4, counts.Total);
    }

    [Fact]
    public void No_errors_counts_zero_everywhere() =>
        Assert.Equal(new SeverityCounts(0, 0, 0), SeverityCounts.Of([]));

    [Fact]
    public void Lists_the_most_severe_first_including_zeros() =>
        Assert.Equal(
            [(ErrorSeverity.BreaksMeaning, 3), (ErrorSeverity.Understandable, 0), (ErrorSeverity.Polish, 1)],
            new SeverityCounts(3, 0, 1).MostSevereFirst());
}
