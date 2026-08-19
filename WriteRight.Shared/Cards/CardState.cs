namespace WriteRight.Shared.Cards;

/// <summary>Ciclo de vida de um card no deck.</summary>
public enum CardState
{
    /// <summary>Cunhado, nunca revisado. Nasce vencido — revisável na mesma hora.</summary>
    New,

    /// <summary>Nos primeiros passos fixos (1d, 3d) ou reaprendendo depois de um lapso.</summary>
    Learning,

    /// <summary>Graduado: o intervalo passa a crescer pelo fator de facilidade.</summary>
    Review,

    /// <summary>Intervalo longo o bastante pra sair da rotação. Continua visível nas estatísticas.</summary>
    Retired,

    /// <summary>Descartado à mão. Card ruim (erro de digitação classificado como vocabulário,
    /// trecho sem resposta única) — sai da rotação mas não é apagado, pra não voltar a nascer.</summary>
    Discarded,
}

/// <summary>
/// Como o usuário classificou a revisão. <see cref="Again"/> não é escolha dele:
/// é o que o servidor grava quando a resposta digitada não bateu — a diferença
/// entre este deck e um de auto-avaliação pura.
/// </summary>
public enum CardRating
{
    Again,
    Hard,
    Easy,
}
