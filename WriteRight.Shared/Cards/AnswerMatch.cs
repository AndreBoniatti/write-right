using System.Globalization;
using System.Text;

namespace WriteRight.Shared.Cards;

/// <summary>Veredito de uma resposta digitada.</summary>
public enum CardVerdict
{
    Correct,

    /// <summary>Bateu quase — diferença pequena o bastante pra ser digitação, não desconhecimento.
    /// Quem decide se conta é o usuário, olhando o diff.</summary>
    NearMiss,

    Wrong,
}

/// <summary>
/// Compara a resposta digitada com a esperada.
///
/// Existe por causa dos cards longos: 12 dos primeiros 76 têm 4+ palavras
/// ("brings the student closer to", "spent the whole afternoon"). Exigir string
/// idêntica meses depois, com pontuação, transforma acerto em erro e envenena a
/// nota que alimenta o intervalo. Daí a normalização agressiva e, depois dela, a
/// faixa de "quase" — que o servidor não decide sozinho: devolve o veredito e o
/// usuário adjudica.
/// </summary>
public static class AnswerMatch
{
    /// <summary>A partir deste tamanho, a palavra tolera dois deslizes em vez de um.</summary>
    private const int LongWordLength = 8;

    /// <summary>
    /// "Quase" = <b>uma palavra</b> errada por <b>um caractere</b> (dois, se for
    /// palavra longa). A comparação é por PALAVRA, não pela string inteira: em
    /// distância de caracteres "closer than" e "closer to" ficam a 3 edições — perto
    /// o bastante pra passar por engano de digitação, quando na verdade é erro de
    /// preposição, exatamente o tipo de coisa que este app corrige. Contar por
    /// palavra separa tropeço no teclado de resposta trocada.
    /// </summary>
    public static CardVerdict Check(string typed, string expected)
    {
        var a = Normalize(typed);
        var b = Normalize(expected);

        if (a.Length == 0) return CardVerdict.Wrong;
        if (a == b) return CardVerdict.Correct;

        var typedWords = a.Split(' ');
        var expectedWords = b.Split(' ');

        // Palavra a mais ou a menos não é digitação — é outra resposta. Vale inclusive
        // pro artigo esquecido: "Artigo" é uma categoria da taxonomia, não um detalhe.
        if (typedWords.Length != expectedWords.Length) return CardVerdict.Wrong;

        var slips = 0;
        for (var i = 0; i < expectedWords.Length; i++)
        {
            if (typedWords[i] == expectedWords[i]) continue;
            if (++slips > 1) return CardVerdict.Wrong;

            var budget = expectedWords[i].Length > LongWordLength ? 2 : 1;
            if (Distance(typedWords[i], expectedWords[i], budget) > budget) return CardVerdict.Wrong;
        }

        return CardVerdict.NearMiss;
    }

    /// <summary>
    /// Minúsculas, sem acento, sem pontuação de borda, espaços colapsados. Aspas e
    /// apóstrofos tipográficos viram os retos — o texto do modelo usa ’ e ninguém
    /// digita isso.
    /// </summary>
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = true; // começa true pra comer o espaço inicial

        foreach (var raw in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;

            var c = raw switch
            {
                '’' or 'ʼ' or '‘' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                _ => char.ToLowerInvariant(raw),
            };

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }

            // Pontuação some, MENOS o que muda a palavra: apóstrofo ("it's" ≠ "its")
            // e hífen ("high-quality"). Ponto final e vírgula não são conhecimento.
            if (char.IsPunctuation(c) && c is not '\'' and not '-') continue;

            sb.Append(c);
            lastWasSpace = false;
        }

        return sb.ToString().Trim().Trim('-', '\'');
    }

    /// <summary>
    /// Levenshtein com corte: passou do orçamento, para de contar. As strings são
    /// curtas (poucas dezenas de chars), então duas linhas de DP bastam.
    /// </summary>
    private static int Distance(string a, string b, int budget)
    {
        if (Math.Abs(a.Length - b.Length) > budget) return budget + 1;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }

            if (best > budget) return budget + 1;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
