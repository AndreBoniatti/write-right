using WriteRight.Shared;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Data;

/// <summary>
/// Um card de vocabulário, cunhado a partir de um erro real do usuário. Existe porque o
/// loop adaptativo por CATEGORIA não alcança vocabulário: regra generaliza, item léxico
/// não — e erro de vocabulário quase nunca se repete sozinho.
///
/// <b>O conteúdo é COPIADO, não referenciado.</b> O card não é um espelho do erro: a
/// reincidência reescreve o conteúdo inteiro com o fracasso mais recente, e lacuna,
/// dica e alternativas são desenho próprio. Um id de origem apontaria pra um erro que
/// já não é o do card.
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
    /// Outras respostas que também contam como certas ("attorney" pra um card de
    /// "lawyer"). É o que deixa o card existir sem punir sinônimo. Gravado como JSON
    /// (coleção primitiva do EF).
    /// </summary>
    public List<string> Alternatives { get; set; } = new();

    /// <summary>
    /// Dica no idioma de ORIGEM. <b>Obrigatória</b>: sem ela a lacuna não tem resposta
    /// única ("at the ___ center" aceita qualquer coisa), e o card seria errado pra
    /// sempre, contando lapso e sujando a estatística do agendador. Quem filtra é o
    /// <c>CardGate</c>; o tipo e a coluna NOT NULL é que garantem.
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
