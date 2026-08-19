using WriteRight.Shared;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Data;

/// <summary>
/// Um card de vocabulário, cunhado a partir de um erro real do usuário.
///
/// Existe porque o loop adaptativo por CATEGORIA não alcança vocabulário: regra
/// generaliza (aprendeu "excited about", transfere), item léxico não. Gerar mais
/// um texto marcado "WordChoice" não faz reencontrar "colorful façades" — e na
/// prática os erros de vocabulário quase nunca se repetem sozinhos. Só um deck
/// explícito força a repetição.
///
/// <b>O conteúdo é COPIADO, não referenciado.</b> Existe DELETE de prática com
/// cascade; se o card apontasse pro erro de origem, apagar uma prática mataria
/// cards em revisão há meses. Mesma decisão (e mesmo motivo) do snapshot de
/// evidências da análise — e o motivo de não haver nem sequer um id de origem
/// aqui: ele apontaria pra uma linha que pode não existir, o que é pior que não
/// ter referência nenhuma.
/// </summary>
public class VocabCard
{
    public int Id { get; set; }

    // ── Snapshot do conteúdo ────────────────────────────────

    /// <summary>Par de idiomas do exercício que gerou o card. Copiado junto porque,
    /// sem a prática de origem, não haveria de onde recuperar.</summary>
    public Language SourceLanguage { get; set; }
    public Language TargetLanguage { get; set; }

    /// <summary>A frase corrigida com a lacuna no lugar da resposta (o cloze).</summary>
    public string Prompt { get; set; } = "";

    /// <summary>O que preenche a lacuna, no idioma-alvo. É o que se digita.</summary>
    public string Answer { get; set; } = "";

    /// <summary>
    /// Dica no idioma de ORIGEM. <b>Obrigatória</b>: sem ela a lacuna não tem resposta
    /// única ("at the ___ center" aceita qualquer coisa), e o card seria errado pra
    /// sempre, contando lapso e sujando a estatística do agendador.
    ///
    /// Não-anulável de propósito, e a coluna é NOT NULL. O erro de origem PODE não ter
    /// trecho correspondente — lá o campo é anulável, porque a ausência significa algo.
    /// Aqui não significa nada: um card sem dica não é um card incompleto, é um card
    /// que não deveria existir. Quem filtra é a cunhagem; o tipo é o que garante.
    /// </summary>
    public string Hint { get; set; } = "";

    /// <summary>O que o usuário escreveu de errado. Âncora de memória — o erro é dele.</summary>
    public string YourAttempt { get; set; } = "";

    public ErrorCategory Category { get; set; }

    // ── Estado do agendamento ───────────────────────────────

    public CardState State { get; set; } = CardState.New;

    /// <summary>Quando o card volta. Card novo nasce vencido (revisável já).</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>Intervalo atual em dias — a base do próximo cálculo.</summary>
    public double IntervalDays { get; set; }

    /// <summary>Fator de facilidade do SM-2. Ver <c>CardScheduler</c> pros limites.</summary>
    public double Ease { get; set; } = CardScheduler.StartingEase;

    /// <summary>Revisões feitas (acertos e erros).</summary>
    public int Reps { get; set; }

    /// <summary>Vezes que o card foi esquecido depois de já ter sido acertado.</summary>
    public int Lapses { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastReviewedAt { get; set; }

    public List<CardReview> Reviews { get; set; } = new();
}
