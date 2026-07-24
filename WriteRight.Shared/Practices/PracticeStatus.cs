namespace WriteRight.Shared.Practices;

/// <summary>
/// Ciclo de vida de uma prática. Persistido como STRING (ver DbContext), igual
/// aos demais enums, pra legibilidade e resiliência a reordenação.
/// </summary>
public enum PracticeStatus
{
    /// <summary>Criada/gerada, ainda não corrigida — aparece na listagem e pode ser retomada.</summary>
    InProgress,

    /// <summary>Corrigida — somente leitura.</summary>
    Completed,
}
