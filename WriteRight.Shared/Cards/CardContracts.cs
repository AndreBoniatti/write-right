using System.Collections.Generic;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Cards;

/// <summary>
/// Um card na sessão de revisão. Repare no que NÃO está aqui: a resposta. Ela só
/// chega depois de você digitar (<c>POST /api/cards/{id}/check</c>) — do contrário
/// bastaria abrir o DevTools pra "lembrar", e o card viraria leitura passiva.
/// </summary>
public sealed record CardReviewItem(
    int Id,
    string Prompt,
    /// <summary>Sempre presente — card sem dica não é cunhado. Ver <c>VocabCard.Hint</c>.</summary>
    string Hint,
    ErrorCategory Category,
    Language SourceLanguage,
    Language TargetLanguage,
    CardState State,
    int Reps,
    int Lapses);

/// <summary>O que você digitou, pra conferência.</summary>
public sealed record CardCheckRequest(string TypedAnswer);

/// <summary>
/// Veredito + a revelação. Não muda nada no banco: quem agenda é o
/// <see cref="CardReviewRequest"/>, depois que o usuário decide o rating (ou
/// adjudica um <see cref="CardVerdict.NearMiss"/>).
/// </summary>
/// <param name="AgainDays">Intervalo se o card contar como erro.</param>
/// <param name="HardDays">Intervalo de um acerto "difícil".</param>
/// <param name="EasyDays">Intervalo de um acerto "fácil".</param>
/// <remarks>
/// As três hipóteses vêm calculadas do servidor, pelo MESMO
/// <c>CardScheduler</c> que vai agendar de verdade — em vez de o cliente refazer
/// a conta e as duas versões poderem discordar. Sem custo: são três chamadas de
/// função pura dentro de uma requisição que já estava acontecendo.
///
/// É o intervalo AGENDADO. Um card com irmãos pode aparecer um pouco depois
/// disso, porque a frase entrega um card por dia.
/// </remarks>
public sealed record CardCheckResult(
    CardVerdict Verdict,
    string Answer,
    string YourAttempt,
    double AgainDays,
    double HardDays,
    double EasyDays);

/// <summary>
/// Fecha a revisão: agenda o card e grava a linha do log.
/// <paramref name="WasCorrect"/> vem do cliente porque no caso "quase" a decisão
/// é legitimamente do usuário — ele viu o diff e sabe se errou ou tropeçou na tecla.
/// </summary>
public sealed record CardReviewRequest(
    string TypedAnswer,
    bool WasCorrect,
    CardRating Rating);

/// <summary>Onde o card foi parar, e quanto ainda falta na sessão.</summary>
public sealed record CardReviewResult(
    CardState State,
    double IntervalDays,
    DateTimeOffset DueAt,
    int RemainingDue);

/// <summary>Contadores do deck — alimenta o ponteiro "N cards para hoje".</summary>
/// <param name="Due">
/// Cards que a PRÓXIMA SESSÃO entrega — não todo card com data vencida. Cards
/// irmãos (mesma frase de origem) esperam a próxima rodada, e anunciar aqui um
/// número que a sessão não cumpre seria pior que anunciar o menor.
/// </param>
public sealed record DeckSummary(
    int Total,
    int New,
    int Due,
    int Learning,
    int Review,
    int Retired,
    int Leeches);

/// <summary>Card na listagem do deck — aqui a resposta aparece: é tela de leitura, não de teste.</summary>
public sealed record DeckCard(
    int Id,
    string Prompt,
    string Answer,
    string Hint,
    string YourAttempt,
    ErrorCategory Category,
    CardState State,
    DateTimeOffset? DueAt,
    double IntervalDays,
    int Reps,
    int Lapses,
    bool IsLeech);

/// <summary>A listagem completa: contadores + os cards.</summary>
public sealed record DeckView(DeckSummary Summary, IReadOnlyList<DeckCard> Cards);
