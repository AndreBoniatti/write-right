using System.Text.RegularExpressions;
using WriteRight.Shared.Cards;

namespace WriteRight.Api.Services;

/// <summary>
/// Monta a frente do card: a frase corrigida com uma lacuna no lugar da resposta.
///
/// Por string matching, sem chamada de IA — medido em 76 erros de vocabulário
/// reais, a correção aparece literal no texto corrigido em 75 (99%). Pagar um
/// modelo pra recortar o que um <c>IndexOf</c> acha não se justifica; e como é
/// determinístico, roda retroativo em quem já está no banco.
///
/// Quando não dá, devolve null e o card simplesmente NÃO NASCE. É de propósito:
/// um card com frente ruim é pior que card nenhum — vai ser respondido errado
/// para sempre e envenenar as estatísticas do agendador.
/// </summary>
internal static partial class ClozeBuilder
{
    /// <summary>
    /// Palavras que a frase precisa ter ALÉM da resposta. Sem isso "___." vira um
    /// card, e a lacuna sem contexto não tem resposta única.
    /// </summary>
    private const int MinimumContextWords = 3;

    private static readonly char[] SentenceEnders = ['.', '!', '?', '\n'];

    /// <summary>
    /// A frase de <paramref name="correctedText"/> que contém
    /// <paramref name="correction"/>, com a ocorrência trocada pela lacuna.
    /// Null quando o trecho não aparece no texto ou a frase não tem contexto.
    /// </summary>
    public static string? Build(string correctedText, string correction)
    {
        if (string.IsNullOrWhiteSpace(correctedText) || string.IsNullOrWhiteSpace(correction))
            return null;

        correction = correction.Trim();

        // Case-insensitive porque a correção pode vir do meio da frase e o texto
        // ter a palavra no início (maiúscula) — é a mesma resposta.
        var index = correctedText.IndexOf(correction, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var (start, end) = SentenceBounds(correctedText, index, correction.Length);
        var sentence = correctedText[start..end];

        // Só a ocorrência que o erro aponta vira lacuna. Se a mesma expressão
        // reaparece na frase, a segunda fica visível — e serve de pista legítima.
        var offset = index - start;
        var cloze = string.Concat(
            sentence[..offset], Cloze.Blank, sentence[(offset + correction.Length)..]).Trim();

        return HasContext(cloze) ? cloze : null;
    }

    /// <summary>Limites da frase que envolve o trecho (recorte por pontuação forte).</summary>
    private static (int Start, int End) SentenceBounds(string text, int index, int length)
    {
        var start = index == 0 ? -1 : text.LastIndexOfAny(SentenceEnders, index - 1);
        start = start < 0 ? 0 : start + 1;

        var after = index + length;
        var end = after >= text.Length ? -1 : text.IndexOfAny(SentenceEnders, after);
        end = end < 0 ? text.Length : end + 1;

        // Espaço à esquerda entra no recorte quando a frase anterior termina colada;
        // o Trim do chamador resolve, mas os índices precisam continuar válidos.
        return (start, end);
    }

    private static bool HasContext(string cloze) =>
        WordPattern().Matches(cloze.Replace(Cloze.Blank, " ")).Count >= MinimumContextWords;

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordPattern();
}
