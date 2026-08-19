using WriteRight.Shared.Cards;

namespace WriteRight.Api.Data;

/// <summary>
/// Uma revisão de card, append-only. O <see cref="VocabCard"/> guarda só o estado
/// ATUAL — cada revisão sobrescreve a anterior — então sem este log não há como
/// responder a única pergunta que diz se o agendador funciona:
///
/// <i>"quando um card volta depois de N dias, qual a taxa de acerto?"</i>
///
/// Alvo ~85%. Muito acima disso, os intervalos estão curtos e há revisão à toa;
/// muito abaixo, estão longos e o card foi esquecido antes de voltar. Responder
/// exige o par (intervalo vigente, acertou ou não) no momento de CADA revisão,
/// que é exatamente o que o estado atual não preserva.
///
/// Escrito desde o início porque o custo é assimétrico: adicionar depois só dá
/// dados dali pra frente — o histórico anterior não se reconstrói.
/// </summary>
public class CardReview
{
    public int Id { get; set; }

    public int VocabCardId { get; set; }
    public VocabCard? VocabCard { get; set; }

    public DateTimeOffset ReviewedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>O que o usuário digitou, literal. Serve pra reler os erros depois.</summary>
    public string TypedAnswer { get; set; } = "";

    /// <summary>Se contou como acerto (inclui o "quase" aceito pelo usuário).</summary>
    public bool WasCorrect { get; set; }

    /// <summary>Como o usuário classificou. <c>Again</c> quando errou (não é escolha dele).</summary>
    public CardRating Rating { get; set; }

    /// <summary>Intervalo vigente quando o card apareceu — a metade da pergunta que o card não guarda.</summary>
    public double IntervalBefore { get; set; }

    /// <summary>Intervalo que o agendador escolheu em seguida.</summary>
    public double IntervalAfter { get; set; }
}
