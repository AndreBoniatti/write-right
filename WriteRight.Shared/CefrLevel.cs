namespace WriteRight.Shared;

/// <summary>Nível de dificuldade (Common European Framework of Reference).</summary>
public enum CefrLevel
{
    A1,
    A2,
    B1,
    B2,
    C1,
    C2,
}

/// <summary>
/// Rótulos de dificuldade (PT) pros níveis CEFR — o código sozinho (ex.: "B1")
/// não diz nada pra quem não conhece a escala. Apresentação, usado pela UI.
/// </summary>
public static class CefrLevels
{
    /// <summary>Rótulo de dificuldade em português (ex.: "Intermediário").</summary>
    public static string Label(CefrLevel level) => level switch
    {
        CefrLevel.A1 => "Iniciante",
        CefrLevel.A2 => "Básico",
        CefrLevel.B1 => "Intermediário",
        CefrLevel.B2 => "Intermediário alto",
        CefrLevel.C1 => "Avançado",
        CefrLevel.C2 => "Proficiente",
        _ => level.ToString(),
    };

    /// <summary>Código + rótulo (ex.: "B1 · Intermediário").</summary>
    public static string Display(CefrLevel level) => $"{level} · {Label(level)}";
}
