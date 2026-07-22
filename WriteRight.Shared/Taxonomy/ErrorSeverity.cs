namespace WriteRight.Shared.Taxonomy;

/// <summary>
/// Impacto do erro — eixo separado da categoria. Serve pra priorizar o estudo:
/// o que quebra a comunicação vem antes do detalhe fino.
/// </summary>
public enum ErrorSeverity
{
    /// <summary>Compromete o entendimento da mensagem.</summary>
    BreaksMeaning,

    /// <summary>Dá pra entender, mas está gramatical/lexicalmente errado.</summary>
    Understandable,

    /// <summary>Correto e compreensível; é só lapidação/naturalidade.</summary>
    Polish,
}
