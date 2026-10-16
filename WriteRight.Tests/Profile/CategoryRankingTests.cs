using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Profile;

/// <summary>
/// A régua que o perfil, a análise e o resumo da correção dividem. Se ela mudar sem
/// querer, o "ponto mais fraco" muda nas três telas ao mesmo tempo — então cada
/// critério de ordem tem o seu teste.
/// </summary>
public class CategoryRankingTests
{
    private static IReadOnlyList<CategoryWeight> Rank(params (ErrorCategory, ErrorSeverity)[] errors) =>
        CategoryRanking.Rank(errors);

    [Fact]
    public void Nothing_to_rank_is_an_empty_list() => Assert.Empty(Rank());

    [Fact]
    public void Counts_and_weighs_each_category()
    {
        var ranked = Rank(
            (ErrorCategory.Article, ErrorSeverity.BreaksMeaning),
            (ErrorCategory.Article, ErrorSeverity.Polish));

        var article = Assert.Single(ranked);
        Assert.Equal(ErrorCategory.Article, article.Category);
        Assert.Equal(2, article.Count);
        Assert.Equal(SeverityWeight.Of(ErrorSeverity.BreaksMeaning) + SeverityWeight.Of(ErrorSeverity.Polish), article.Score);
    }

    /// <summary>Um erro que quebra o sentido pesa mais que dois de lapidação.</summary>
    [Fact]
    public void Weight_comes_before_volume()
    {
        var ranked = Rank(
            (ErrorCategory.Spelling, ErrorSeverity.Polish),
            (ErrorCategory.Spelling, ErrorSeverity.Polish),
            (ErrorCategory.Preposition, ErrorSeverity.BreaksMeaning));

        Assert.Equal([ErrorCategory.Preposition, ErrorCategory.Spelling], ranked.Select(c => c.Category));
    }

    /// <summary>Mesmo peso: quem aparece mais vem antes (dois de lapidação contra um compreensível).</summary>
    [Fact]
    public void On_equal_weight_volume_breaks_the_tie()
    {
        var ranked = Rank(
            (ErrorCategory.Article, ErrorSeverity.Understandable),
            (ErrorCategory.Spelling, ErrorSeverity.Polish),
            (ErrorCategory.Spelling, ErrorSeverity.Polish));

        Assert.Equal([ErrorCategory.Spelling, ErrorCategory.Article], ranked.Select(c => c.Category));
    }

    /// <summary>Empate total: a ordem do enum decide, não a ordem em que os erros chegaram.</summary>
    [Fact]
    public void A_full_tie_is_decided_by_the_enum_not_by_arrival()
    {
        var ranked = Rank(
            (ErrorCategory.Spelling, ErrorSeverity.Understandable),
            (ErrorCategory.Article, ErrorSeverity.Understandable));

        Assert.Equal([ErrorCategory.Article, ErrorCategory.Spelling], ranked.Select(c => c.Category));
    }
}
