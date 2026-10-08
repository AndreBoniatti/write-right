using System.Text.RegularExpressions;
using WriteRight.Shared.Cards;

namespace WriteRight.Api.Services;

/// <summary>
/// Monta a frente do card: a frase corrigida com uma lacuna no lugar da resposta.
///
/// Por string matching, sem chamada de IA: quem escolhe a resposta é o passo de
/// desenho de cards, e ela vem pedida como trecho LITERAL do texto corrigido — então
/// achar a lacuna é um <c>IndexOf</c> por palavra inteira, não um julgamento.
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

        var index = WholeWordIndexOf(correctedText, correction);
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

    /// <summary>
    /// Primeira ocorrência de <paramref name="value"/> como palavra(s) INTEIRA(s): letra
    /// ou número colado antes ou depois desclassifica a ocorrência. Sem isto a resposta
    /// curta "law" virava a lacuna dentro de "lawyer" — "The ___yer advised…" — e isso
    /// aconteceu de verdade, na avaliação de 2026-10-07. Quanto mais curta a resposta,
    /// mais fácil ela aparecer dentro de outra palavra.
    ///
    /// Case-insensitive porque a resposta pode vir do meio da frase e o texto ter a
    /// palavra no início (maiúscula) — é a mesma resposta.
    /// </summary>
    internal static int WholeWordIndexOf(string text, string value)
    {
        for (var i = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
             i >= 0;
             i = text.IndexOf(value, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + value.Length;
            var cleanBefore = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var cleanAfter = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (cleanBefore && cleanAfter) return i;
        }
        return -1;
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
