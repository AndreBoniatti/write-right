namespace WriteRight.Shared.Taxonomy;

/// <summary>
/// Peso de estudo por gravidade — o que atrapalha a comunicação vale mais que a
/// lapidação. Fonte única: o perfil ordena por ele, a análise escolhe as categorias
/// que vão pro modelo por ele, e a legenda da UI descreve estes mesmos números.
/// </summary>
public static class SeverityWeight
{
    public static int Of(ErrorSeverity severity) => severity switch
    {
        ErrorSeverity.BreaksMeaning => 3,
        ErrorSeverity.Understandable => 2,
        ErrorSeverity.Polish => 1,
        _ => 1,
    };
}
