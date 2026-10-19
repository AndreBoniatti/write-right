using System;
using System.Collections.Generic;
using System.Linq;

namespace WriteRight.Shared.Profile;

/// <summary>As primeiras práticas contra as últimas, na mesma régua (erros a cada 10 palavras).</summary>
public sealed record RateComparison(double First, double Last);

/// <summary>
/// A régua do gráfico de evolução: erros a cada <see cref="PerWords"/> palavras.
///
/// Por palavra, e não por prática, porque o tamanho do texto varia. No histórico real
/// os textos encolheram de ~25 pra ~16 palavras: a contagem por prática caiu de 4,9
/// pra 4,2 enquanto a taxa por palavra subiu de 2,0 pra 2,6. Contando por prática, a
/// tela mostrava melhora onde havia piora.
/// </summary>
public static class ErrorRate
{
    public const int PerWords = 10;

    /// <summary>Tamanho da média móvel e de cada ponta da comparação primeiras × últimas.</summary>
    public const int Window = 10;

    /// <summary>Com menos práticas que isso, a "média" seria só a própria prática.</summary>
    public const int MinimumForAverage = 5;

    /// <summary>Palavras de um texto: pedaços separados por espaço em branco.</summary>
    public static int Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Erros a cada <see cref="PerWords"/> palavras no conjunto: soma dos erros ÷ soma
    /// das palavras, não a média das taxas — um texto de 8 palavras com 3 erros não
    /// pode pesar o mesmo que um de 36. Null quando não há palavra nenhuma.
    /// </summary>
    public static double? Of(IEnumerable<PracticePoint> points)
    {
        int errors = 0, words = 0;
        foreach (var p in points)
        {
            errors += p.Errors.Total;
            words += p.Words;
        }
        return words == 0 ? null : errors * (double)PerWords / words;
    }

    /// <summary>
    /// A média móvel: pra cada prática, a taxa das últimas <see cref="Window"/> até ela
    /// (inclusive). Null nas primeiras <see cref="MinimumForAverage"/> − 1.
    /// </summary>
    public static IReadOnlyList<double?> Rolling(IReadOnlyList<PracticePoint> points) =>
        points
            .Select((_, i) => i + 1 < MinimumForAverage
                ? null
                : Of(points.Skip(Math.Max(0, i + 1 - Window)).Take(Math.Min(Window, i + 1))))
            .ToList();

    /// <summary>
    /// As primeiras <see cref="Window"/> práticas contra as últimas <see cref="Window"/>.
    /// Null enquanto as duas pontas se sobrepõem: aí seria comparar a prática com ela mesma.
    /// </summary>
    public static RateComparison? FirstVersusLast(IReadOnlyList<PracticePoint> points)
    {
        if (points.Count < 2 * Window) return null;
        var first = Of(points.Take(Window));
        var last = Of(points.Skip(points.Count - Window));
        return first is null || last is null ? null : new(first.Value, last.Value);
    }
}
