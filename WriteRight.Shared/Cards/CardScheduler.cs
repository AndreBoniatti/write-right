namespace WriteRight.Shared.Cards;

/// <summary>
/// Estado de agendamento de um card — só os campos que o agendador lê e escreve.
/// Separado da entidade de propósito: assim a política é uma função pura, testável
/// sem banco, e a entidade continua sendo só persistência.
/// </summary>
public sealed record CardSchedule(
    CardState State,
    double IntervalDays,
    double Ease,
    int Reps,
    int Lapses,
    DateTimeOffset DueAt)
{
    /// <summary>Estado de um card recém-cunhado: vencido agora, sem intervalo.</summary>
    public static CardSchedule New(DateTimeOffset now) =>
        new(CardState.New, 0, CardScheduler.StartingEase, 0, 0, now);
}

/// <summary>
/// SM-2 enxuto: dado o estado atual e como foi a revisão, devolve o próximo estado.
///
/// É chato de propósito. O valor deste módulo está no CONTEÚDO do card (nasce de
/// um erro real, com a frase em volta), não na aritmética do intervalo — escrever
/// FSRS aqui seria esforço no pedaço já resolvido do problema. Se o log de revisões
/// mostrar acerto muito fora de ~85% num intervalo, mexe-se nas constantes.
/// </summary>
public static class CardScheduler
{
    public const double StartingEase = 2.5;
    public const double MinimumEase = 1.3;

    /// <summary>Passos fixos antes de o card graduar (em dias).</summary>
    public const double FirstStepDays = 1;
    public const double SecondStepDays = 3;

    /// <summary>Multiplicador de um acerto "difícil" — cresce, mas bem menos que o ease.</summary>
    public const double HardMultiplier = 1.2;

    public const double EasyBonus = 0.15;
    public const double HardPenalty = 0.15;
    public const double LapsePenalty = 0.20;

    /// <summary>A partir daqui o card sai da rotação (com <see cref="RetirementReps"/> revisões).</summary>
    public const double RetirementIntervalDays = 180;
    public const int RetirementReps = 6;

    /// <summary>Lapsos que marcam um card como travado — não está entrando, vale reescrever ou descartar.</summary>
    public const int LeechLapses = 8;

    /// <summary>
    /// Próximo agendamento. <paramref name="wasCorrect"/> vem da resposta digitada
    /// (objetivo); <paramref name="rating"/> só afina o intervalo quando acertou.
    /// </summary>
    public static CardSchedule Next(
        CardSchedule current, bool wasCorrect, CardRating rating, DateTimeOffset now)
    {
        var reps = current.Reps + 1;

        if (!wasCorrect)
        {
            // Lapso só conta pra quem já tinha graduado: errar um card que você nunca
            // acertou não é esquecer, é ainda não ter aprendido. Misturar os dois
            // inflaria o contador de leech e marcaria como "travado" card que só é novo.
            var lapses = current.State == CardState.Review || current.State == CardState.Retired
                ? current.Lapses + 1
                : current.Lapses;

            return new CardSchedule(
                CardState.Learning,
                FirstStepDays,
                Math.Max(MinimumEase, current.Ease - LapsePenalty),
                reps,
                lapses,
                now.AddDays(FirstStepDays));
        }

        var ease = rating == CardRating.Easy
            ? current.Ease + EasyBonus
            : Math.Max(MinimumEase, current.Ease - HardPenalty);

        // Os dois primeiros passos são FIXOS, independentes de fácil/difícil: nas
        // primeiras exposições o card ainda está sendo memorizado e a auto-avaliação
        // não carrega informação suficiente pra mexer no intervalo. O rating começa a
        // valer quando o card gradua — mas o ease já se ajusta desde a primeira.
        double interval;
        if (current.IntervalDays < FirstStepDays) interval = FirstStepDays;
        else if (current.IntervalDays < SecondStepDays) interval = SecondStepDays;
        else interval = current.IntervalDays * (rating == CardRating.Easy ? ease : HardMultiplier);

        var state = interval >= RetirementIntervalDays && reps >= RetirementReps
            ? CardState.Retired
            : interval >= SecondStepDays ? CardState.Review : CardState.Learning;

        return new CardSchedule(state, interval, ease, reps, current.Lapses, now.AddDays(interval));
    }

    /// <summary>Card que não está entrando — candidato a reescrita ou descarte.</summary>
    public static bool IsLeech(int lapses) => lapses >= LeechLapses;
}
