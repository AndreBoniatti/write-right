using WriteRight.Shared.Cards;

namespace WriteRight.Tests.Cards;

/// <summary>
/// A aritmética dos intervalos. É a parte chata do módulo de propósito (o valor
/// está no conteúdo do card), mas é onde um sinal trocado passa despercebido por
/// meses: um intervalo errado não quebra nada visível, só faz a revisão chegar na
/// hora errada. Daí valer teste.
/// </summary>
public class CardSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    private static CardSchedule Fresh() => CardSchedule.New(Now);

    private static CardSchedule Graduated(double interval = 10, double ease = CardScheduler.StartingEase,
        int reps = 3, int lapses = 0) =>
        new(CardState.Review, interval, ease, reps, lapses, Now);

    [Fact]
    public void First_correct_answer_schedules_the_first_step()
    {
        var next = CardScheduler.Next(Fresh(), wasCorrect: true, CardRating.Easy, Now);

        Assert.Equal(CardScheduler.FirstStepDays, next.IntervalDays);
        Assert.Equal(CardState.Learning, next.State);
        Assert.Equal(Now.AddDays(CardScheduler.FirstStepDays), next.DueAt);
    }

    [Fact]
    public void Second_correct_answer_schedules_the_second_step_and_graduates()
    {
        var first = CardScheduler.Next(Fresh(), true, CardRating.Hard, Now);
        var second = CardScheduler.Next(first, true, CardRating.Hard, Now);

        Assert.Equal(CardScheduler.SecondStepDays, second.IntervalDays);
        Assert.Equal(CardState.Review, second.State);
    }

    /// <summary>
    /// Os dois primeiros passos são fixos por decisão: nas primeiras exposições a
    /// auto-avaliação não carrega informação suficiente pra mexer no intervalo. O
    /// ease, esse sim, já se ajusta desde a primeira revisão.
    /// </summary>
    [Fact]
    public void Rating_does_not_move_the_learning_steps_but_does_move_the_ease()
    {
        var easy = CardScheduler.Next(Fresh(), true, CardRating.Easy, Now);
        var hard = CardScheduler.Next(Fresh(), true, CardRating.Hard, Now);

        Assert.Equal(easy.IntervalDays, hard.IntervalDays);
        Assert.True(easy.Ease > hard.Ease);
    }

    [Fact]
    public void After_graduation_easy_multiplies_by_the_ease_and_hard_by_the_smaller_factor()
    {
        var easy = CardScheduler.Next(Graduated(interval: 10), true, CardRating.Easy, Now);
        var hard = CardScheduler.Next(Graduated(interval: 10), true, CardRating.Hard, Now);

        // Easy usa o ease JÁ ajustado (2.5 + 0.15), não o anterior.
        Assert.Equal(10 * (CardScheduler.StartingEase + CardScheduler.EasyBonus), easy.IntervalDays, 6);
        Assert.Equal(10 * CardScheduler.HardMultiplier, hard.IntervalDays, 6);
    }

    [Fact]
    public void A_wrong_answer_resets_to_the_first_step_and_lowers_the_ease()
    {
        var next = CardScheduler.Next(Graduated(interval: 40), false, CardRating.Again, Now);

        Assert.Equal(CardScheduler.FirstStepDays, next.IntervalDays);
        Assert.Equal(CardState.Learning, next.State);
        Assert.Equal(CardScheduler.StartingEase - CardScheduler.LapsePenalty, next.Ease, 6);
    }

    /// <summary>
    /// Lapso é ESQUECER, não "ainda não aprendi". Contar erro em card novo inflaria
    /// o contador e marcaria como travado (leech) um card que só é recente.
    /// </summary>
    [Fact]
    public void Only_a_graduated_card_counts_a_lapse()
    {
        var newCard = CardScheduler.Next(Fresh(), false, CardRating.Again, Now);
        var graduated = CardScheduler.Next(Graduated(), false, CardRating.Again, Now);

        Assert.Equal(0, newCard.Lapses);
        Assert.Equal(1, graduated.Lapses);
    }

    [Fact]
    public void Ease_never_falls_below_the_floor()
    {
        var schedule = Graduated(ease: CardScheduler.MinimumEase);

        for (var i = 0; i < 5; i++)
            schedule = CardScheduler.Next(schedule, false, CardRating.Again, Now);

        Assert.Equal(CardScheduler.MinimumEase, schedule.Ease, 6);
    }

    [Fact]
    public void A_long_enough_interval_retires_the_card()
    {
        var next = CardScheduler.Next(
            Graduated(interval: CardScheduler.RetirementIntervalDays, reps: CardScheduler.RetirementReps),
            true, CardRating.Easy, Now);

        Assert.Equal(CardState.Retired, next.State);
    }

    /// <summary>
    /// Intervalo longo com poucas revisões não aposenta: as duas condições existem
    /// justamente pra um card não sumir por ter tido sorte em duas respostas.
    /// </summary>
    [Fact]
    public void A_long_interval_with_few_reps_does_not_retire()
    {
        var next = CardScheduler.Next(
            Graduated(interval: CardScheduler.RetirementIntervalDays, reps: 1),
            true, CardRating.Easy, Now);

        Assert.Equal(CardState.Review, next.State);
    }

    [Fact]
    public void Every_review_counts_a_rep()
    {
        var next = CardScheduler.Next(Graduated(reps: 7), false, CardRating.Again, Now);
        Assert.Equal(8, next.Reps);
    }

    [Theory]
    [InlineData(CardScheduler.LeechLapses - 1, false)]
    [InlineData(CardScheduler.LeechLapses, true)]
    public void Leech_is_flagged_at_the_threshold(int lapses, bool expected)
    {
        Assert.Equal(expected, CardScheduler.IsLeech(lapses));
    }
}
