using WriteRight.Shared.Analysis;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Analysis;

/// <summary>
/// O aviso de análise velha. Se a conta escorregar, a tela passa a anunciar como
/// mudança o que é só ruído de amostra pequena, ou a esconder o que cresceu.
/// </summary>
public class AnalysisDriftTests
{
    private static readonly HashSet<ErrorCategory> Covered =
        [ErrorCategory.Preposition, ErrorCategory.VerbTense];

    private static List<ErrorCategory> Many(params (ErrorCategory Category, int Count)[] groups) =>
        groups.SelectMany(g => Enumerable.Repeat(g.Category, g.Count)).ToList();

    [Fact]
    public void Is_null_while_the_new_material_is_smaller_than_what_the_analysis_read()
    {
        var read = Many((ErrorCategory.Preposition, 10));

        Assert.Null(AnalysisDrift.Measure(Covered, read, Many((ErrorCategory.Spelling, 9))));
        Assert.NotNull(AnalysisDrift.Measure(Covered, read, Many((ErrorCategory.Spelling, 10))));
    }

    [Fact]
    public void Is_null_when_the_analysis_read_nothing()
    {
        Assert.Null(AnalysisDrift.Measure(Covered, [], Many((ErrorCategory.Spelling, 5))));
    }

    [Fact]
    public void Measures_the_covered_share_on_both_sides()
    {
        var read = Many((ErrorCategory.Preposition, 3), (ErrorCategory.VerbTense, 1), (ErrorCategory.Spelling, 1));
        var since = Many((ErrorCategory.Preposition, 1), (ErrorCategory.Spelling, 3), (ErrorCategory.Article, 1));

        var drift = AnalysisDrift.Measure(Covered, read, since)!;

        Assert.Equal(0.8, drift.CoveredShareRead, 6);
        Assert.Equal(0.2, drift.CoveredShareSince, 6);
    }

    [Fact]
    public void Names_the_most_frequent_uncovered_categories_over_all_new_errors()
    {
        // O denominador é TODO erro novo, coberto ou não: "Ortografia é 30% dos seus erros".
        var read = Many((ErrorCategory.Preposition, 10));
        var since = Many(
            (ErrorCategory.Preposition, 2),
            (ErrorCategory.Spelling, 3),
            (ErrorCategory.Pronoun, 2),
            (ErrorCategory.Article, 2),
            (ErrorCategory.Punctuation, 1));

        var top = AnalysisDrift.Measure(Covered, read, since)!.TopUncoveredSince;

        // Teto de 3, e o empate Artigo × Pronome sai na ordem do catálogo.
        Assert.Equal(
            new[] { ErrorCategory.Spelling, ErrorCategory.Article, ErrorCategory.Pronoun },
            top.Select(c => c.Category));
        Assert.Equal(0.3, top[0].Share, 6);
        Assert.DoesNotContain(top, c => Covered.Contains(c.Category));
    }

    [Fact]
    public void Lists_no_uncovered_category_when_every_new_error_is_covered()
    {
        var read = Many((ErrorCategory.Preposition, 4));
        var since = Many((ErrorCategory.VerbTense, 4));

        var drift = AnalysisDrift.Measure(Covered, read, since)!;

        Assert.Empty(drift.TopUncoveredSince);
        Assert.Equal(1.0, drift.CoveredShareSince, 6);
    }
}
