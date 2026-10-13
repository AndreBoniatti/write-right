namespace WriteRight.Shared.Corrections;

/// <summary>Um pedaço do texto corrigido, na ordem em que aparece.</summary>
public abstract record MarkupSegment;

/// <summary>Trecho que a correção manteve como o usuário escreveu.</summary>
public sealed record UnchangedText(string Text) : MarkupSegment;

/// <summary>
/// Trecho que a correção mudou: <see cref="Removed"/> é o que o usuário escreveu,
/// <see cref="Inserted"/> é o que entrou no lugar (um dos dois pode ser vazio).
/// <see cref="Errors"/> são os índices dos erros que explicam a mudança; vazio quando
/// o corretor mudou o texto sem registrar erro nenhum.
/// </summary>
public sealed record ChangedText(string Removed, string Inserted, IReadOnlyList<int> Errors) : MarkupSegment
{
    /// <summary>
    /// Remoção pura de palavra. O texto ao redor vem do corrigido, onde a palavra não
    /// existe — então nenhum espaço sobra pra ela, e quem desenha precisa pôr um antes.
    /// Pontuação removida ("birthday, and") cola na palavra anterior e dispensa.
    /// </summary>
    public bool IsWordRemoval => Inserted.Length == 0 && Removed.Length > 0 && char.IsLetterOrDigit(Removed[0]);
}

/// <summary>
/// O texto corrigido com cada mudança marcada no lugar e presa aos erros que a explicam.
///
/// Parte da DIFERENÇA palavra a palavra entre tradução e corrigido, não da lista de
/// erros: nas práticas reais, 20 de 45 têm erros sobrepostos ("appreciate a lot this
/// convivency moments" contém outros três), e trocar erro a erro repetiria trechos. A
/// diferença é linear por construção e ainda mostra o que o corretor mudou sem
/// registrar (11 de 244 trechos). Medido em 46 práticas: 243 de 243 erros localizados.
/// </summary>
public sealed record CorrectionMarkup(IReadOnlyList<MarkupSegment> Segments, IReadOnlyList<int> UnlocatedErrors)
{
    public static CorrectionMarkup Build(string userTranslation, string correctedText, IReadOnlyList<WritingError> errors)
    {
        var mine = Tokenize(userTranslation);
        var fixedTokens = Tokenize(correctedText);
        var hunks = Diff(mine, fixedTokens)
            .Select(h => Hunk.From(h, mine, fixedTokens, userTranslation, correctedText))
            .ToList();

        var unlocated = new List<int>();
        for (var index = 0; index < errors.Count; index++)
        {
            var touched = Locate(errors[index], hunks, userTranslation, correctedText);
            if (touched is null) unlocated.Add(index);
            else touched.ForEach(h => h.Errors.Add(index));
        }

        var segments = new List<MarkupSegment>();
        var at = 0;
        foreach (var h in hunks)
        {
            if (h.FixedStart > at) segments.Add(new UnchangedText(correctedText[at..h.FixedStart]));
            segments.Add(new ChangedText(h.Removed, h.Inserted, h.Errors));
            at = h.FixedEnd;
        }
        if (at < correctedText.Length) segments.Add(new UnchangedText(correctedText[at..]));

        return new CorrectionMarkup(segments, unlocated);
    }

    /// <summary>
    /// Acha os trechos que um erro explica. Primeiro pelo original na tradução; se ele
    /// se repete ("stayed" duas vezes), vale a ocorrência cuja mudança traz a correção.
    /// Se não aparece — o modelo às vezes escreve o original já meio corrigido ("on the
    /// space center" pra "on the spacial center") —, procura a correção no corrigido.
    /// </summary>
    private static List<Hunk>? Locate(WritingError error, List<Hunk> hunks, string userTranslation, string correctedText)
    {
        var wanted = Words(error.Correction);
        List<Hunk>? best = null;
        var bestScore = -1;

        foreach (var start in Occurrences(userTranslation, error.Original))
        {
            var end = start + error.Original.Length;
            var touched = hunks.Where(h => h.TouchesMine(start, end)).ToList();
            if (touched.Count == 0) continue;

            var inserted = touched.SelectMany(h => Words(h.Inserted)).ToHashSet();
            var score = wanted.Count(inserted.Contains);
            if (score > bestScore) (best, bestScore) = (touched, score);
        }
        if (best is not null) return best;

        foreach (var start in Occurrences(correctedText, error.Correction))
        {
            var end = start + error.Correction.Length;
            var touched = hunks.Where(h => h.TouchesFixed(start, end)).ToList();
            if (touched.Count > 0) return touched;
        }
        return null;
    }

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            yield return i;
    }

    private static List<string> Words(string text) =>
        Tokenize(text.ToLowerInvariant()).Select(t => t.Text).ToList();

    private readonly record struct Token(string Text, int Start, int End);

    /// <summary>
    /// Palavras e pontuação, cada sinal um token; espaço separa e some. Contração
    /// ("don't", "Maria's") é uma palavra só — o apóstrofo muda a palavra, e o
    /// "granddaughter → granddaughter's" tem que aparecer como uma troca.
    /// </summary>
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }

            var start = i;
            if (char.IsLetterOrDigit(text[i]))
            {
                while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
                if (i + 1 < text.Length && (text[i] is '\'' or '’') && char.IsLetter(text[i + 1]))
                {
                    i++;
                    while (i < text.Length && char.IsLetter(text[i])) i++;
                }
            }
            else i++;

            tokens.Add(new Token(text[start..i], start, i));
        }
        return tokens;
    }

    private readonly record struct Span(int MineStart, int MineEnd, int FixedStart, int FixedEnd);

    /// <summary>
    /// Maior subsequência comum por palavra, O(n × m) — as práticas têm até ~45
    /// palavras. No empate, apaga antes de inserir: "before to go" → "before going"
    /// sai como uma troca só, em vez de "+going to −go to".
    /// </summary>
    private static List<Span> Diff(List<Token> a, List<Token> b)
    {
        var n = a.Count;
        var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i].Text == b[j].Text ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var spans = new List<Span>();
        Span? open = null;
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x].Text == b[y].Text)
            {
                if (open is { } done) spans.Add(done);
                open = null;
                x++;
                y++;
                continue;
            }

            var current = open ?? new Span(x, x, y, y);
            if (x < n && (y >= m || lcs[x + 1, y] >= lcs[x, y + 1])) current = current with { MineEnd = ++x };
            else current = current with { FixedEnd = ++y };
            open = current;
        }
        if (open is { } last) spans.Add(last);
        return spans;
    }

    /// <summary>Uma mudança com o alcance dela em caracteres nos dois textos.</summary>
    private sealed class Hunk
    {
        public required string Removed { get; init; }
        public required string Inserted { get; init; }

        // Na tradução: o trecho apagado ou, numa inserção pura, o vão entre os vizinhos.
        public required int MineStart { get; init; }
        public required int MineEnd { get; init; }

        // No corrigido: o trecho inserido ou, numa remoção pura, logo após a palavra anterior.
        public required int FixedStart { get; init; }
        public required int FixedEnd { get; init; }

        public List<int> Errors { get; } = [];

        public bool TouchesMine(int start, int end) =>
            Removed.Length > 0 ? MineStart < end && MineEnd > start : MineStart <= end && MineEnd >= start;

        public bool TouchesFixed(int start, int end) =>
            Inserted.Length > 0 ? FixedStart < end && FixedEnd > start : FixedStart >= start && FixedStart <= end;

        public static Hunk From(Span s, List<Token> mine, List<Token> fixedTokens, string userTranslation, string correctedText)
        {
            var removes = s.MineEnd > s.MineStart;
            var inserts = s.FixedEnd > s.FixedStart;

            var mineStart = removes ? mine[s.MineStart].Start : s.MineStart > 0 ? mine[s.MineStart - 1].End : 0;
            var mineEnd = removes ? mine[s.MineEnd - 1].End : s.MineStart < mine.Count ? mine[s.MineStart].Start : userTranslation.Length;
            var fixedStart = inserts ? fixedTokens[s.FixedStart].Start : s.FixedStart > 0 ? fixedTokens[s.FixedStart - 1].End : 0;
            var fixedEnd = inserts ? fixedTokens[s.FixedEnd - 1].End : fixedStart;

            return new Hunk
            {
                Removed = removes ? userTranslation[mineStart..mineEnd] : "",
                Inserted = inserts ? correctedText[fixedStart..fixedEnd] : "",
                MineStart = mineStart,
                MineEnd = mineEnd,
                FixedStart = fixedStart,
                FixedEnd = fixedEnd,
            };
        }
    }
}
