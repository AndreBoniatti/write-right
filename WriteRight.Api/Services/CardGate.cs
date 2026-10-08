using WriteRight.Api.Data;
using WriteRight.Shared.Cards;

namespace WriteRight.Api.Services;

/// <summary>Uma proposta que passou pelo portão: tudo que o card precisa, já conferido.</summary>
internal sealed record GatedCard(
    string Prompt,
    string Answer,
    string Hint,
    IReadOnlyList<string> Alternatives,
    ExerciseError Source);

/// <summary>
/// O que o código confere depois do modelo, antes de qualquer card nascer.
///
/// O modelo decide o que é card; o portão decide se o card que ele desenhou é
/// <b>montável e coerente</b> — coisas que se verificam contra o texto, sem opinião.
/// A regra da casa é a mesma do <see cref="ClozeBuilder"/>: card ruim é pior que card
/// nenhum, então qualquer dúvida mecânica derruba a proposta inteira.
///
/// Os dois limites numéricos foram validados de graça, reprocessando a saída já paga
/// do modelo (2026-10-07): cada um derrubou um card ruim que o modelo deixou passar
/// ("went back home" com 8 alternativas; "brings the student closer to"), ao preço
/// de um card bom ("takes him for a walk"). Evidência pequena — um card cada —, mas o
/// princípio se sustenta sozinho: lista longa de alternativas é ambiguidade com outro
/// nome, e resposta longa é pedaço de frase, não item.
/// </summary>
internal static class CardGate
{
    /// <summary>
    /// Acima disso a resposta deixa de ser item e vira trecho de frase — que se acerta
    /// decorando a string, não sabendo o inglês. O <c>AnswerMatch</c> reprova se o nº
    /// de palavras não bater exato, então cada palavra a mais é uma chance a mais de
    /// errar por motivo nenhum.
    /// </summary>
    public const int MaxAnswerWords = 3;

    /// <summary>Acima disso a lacuna não tem resposta definida, por mais que a lista tente.</summary>
    public const int MaxAlternatives = 4;

    /// <param name="sentErrors">Os erros na ORDEM em que foram enviados ao modelo — a proposta os cita pela posição.</param>
    public static GatedCard? Check(
        ExerciseAttempt practice, IReadOnlyList<ExerciseError> sentErrors, CardProposal proposal)
    {
        if (proposal.Verdict != CardDecisionKind.Card) return null;

        if (proposal.Error < 1 || proposal.Error > sentErrors.Count) return null;
        var source = sentErrors[proposal.Error - 1];

        var answer = proposal.Answer?.Trim() ?? "";
        var hint = proposal.Hint?.Trim() ?? "";
        if (answer.Length == 0 || hint.Length == 0) return null;

        var key = AnswerMatch.Normalize(answer);
        if (key.Split(' ').Length > MaxAnswerWords) return null;

        // Dica que não está no texto de origem foi inventada (ou traduzida de volta), e
        // aí não há garantia de que ela aponte pra essa lacuna. Por palavra inteira,
        // senão "casa" passaria por estar dentro de "casamento".
        if (ClozeBuilder.WholeWordIndexOf(practice.SourceText, hint) < 0) return null;

        var prompt = ClozeBuilder.Build(practice.CorrectedText, answer);
        if (prompt is null) return null;

        // Se o que o aluno escreveu já CONTÉM a resposta — ou uma das aceitas —, ele
        // sabia a palavra e errou outra coisa em volta ("the lawyer" → "a lawyer" não é
        // card de "lawyer"). O card o reprovaria por algo que ele acertou. O prompt
        // proíbe; aqui é a trava. Contra os cards da avaliação, não derrubou nenhum bom.
        var alternatives = (proposal.AlsoCorrect ?? [])
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && AnswerMatch.Normalize(a) != key)
            .DistinctBy(AnswerMatch.Normalize)
            .ToList();

        var wrote = $" {AnswerMatch.Normalize(source.Original)} ";
        if (alternatives.Prepend(answer).Any(a => wrote.Contains($" {AnswerMatch.Normalize(a)} ")))
            return null;

        if (alternatives.Count > MaxAlternatives) return null;

        return new GatedCard(prompt, answer, hint, alternatives, source);
    }
}
