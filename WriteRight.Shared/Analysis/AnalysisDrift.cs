using System.Collections.Generic;
using System.Linq;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Analysis;

/// <summary>Uma categoria e a fração (0–1) dos erros que cai nela.</summary>
public sealed record CategoryShare(ErrorCategory Category, double Share);

/// <summary>
/// Quanto o histórico andou desde a análise, medido no banco, sem IA: que fração dos
/// seus erros cai nas categorias dos padrões, nas práticas da janela dela e nas que
/// vieram depois, e quais categorias que ela não cobre mais aparecem agora.
///
/// O "antes" são TODOS os erros das práticas da janela, não só os das categorias que
/// foram ao modelo: a fração é "dos seus erros naquelas práticas", o mesmo recorte do
/// "depois". Por isso ele passa do <c>ErrorsAnalyzed</c> gravado (no histórico real,
/// 99 erros contra 70 enviados).
///
/// Existe porque a análise é um retrato e envelhece calada. No histórico real, uma
/// análise de 19 práticas seguia na tela 36 práticas depois. Nesse tempo, as categorias
/// dela tinham caído de 41% pra 22% dos erros, e o que crescia não aparecia em lugar nenhum.
/// </summary>
/// <param name="CoveredShareRead">Fração dos erros das práticas da janela que cai nas categorias dos padrões.</param>
/// <param name="CoveredShareSince">A mesma fração nos erros das práticas concluídas depois dela.</param>
/// <param name="TopUncoveredSince">As categorias fora dos padrões que mais aparecem depois dela.</param>
public sealed record AnalysisDrift(
    double CoveredShareRead,
    double CoveredShareSince,
    IReadOnlyList<CategoryShare> TopUncoveredSince)
{
    /// <summary>Quantas categorias descobertas a tela nomeia.</summary>
    public const int TopUncovered = 3;

    /// <summary>
    /// Compara as práticas que a análise leu com as que vieram depois.
    ///
    /// Null enquanto o depois tiver menos erros que o antes: com 3 práticas novas
    /// (~15 erros), uma categoria pula 10 pontos por acaso, e a tela anunciaria uma
    /// mudança que é só ruído. Exigir pelo menos o volume da janela põe os dois lados
    /// da comparação no mesmo pé, sem inventar um piso novo.
    /// </summary>
    /// <param name="covered">As categorias dos padrões da análise.</param>
    /// <param name="read">A categoria de cada erro das práticas da janela.</param>
    /// <param name="since">A categoria de cada erro das práticas concluídas depois dela.</param>
    public static AnalysisDrift? Measure(
        IReadOnlySet<ErrorCategory> covered,
        IReadOnlyList<ErrorCategory> read,
        IReadOnlyList<ErrorCategory> since)
    {
        if (read.Count == 0 || since.Count < read.Count) return null;

        var top = since
            .Where(c => !covered.Contains(c))
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key) // empate: a ordem do catálogo, pra não oscilar entre leituras
            .Take(TopUncovered)
            .Select(g => new CategoryShare(g.Key, (double)g.Count() / since.Count))
            .ToList();

        return new AnalysisDrift(
            (double)read.Count(covered.Contains) / read.Count,
            (double)since.Count(covered.Contains) / since.Count,
            top);
    }
}
