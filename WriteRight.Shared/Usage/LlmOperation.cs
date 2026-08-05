namespace WriteRight.Shared.Usage;

/// <summary>
/// Qual das três chamadas de IA do app gerou o consumo. Vive no <c>Shared</c> (e não
/// no backend) porque o relatório de uso é contrato de API — o cliente precisa do
/// mesmo enum pra rotular a quebra por operação.
///
/// Persistido como STRING, igual ao resto do schema: reordenar o enum não reescreve
/// o histórico.
/// </summary>
public enum LlmOperation
{
    /// <summary>Geração do texto a traduzir (modelo barato).</summary>
    Generation,

    /// <summary>Correção da tradução (modelo bom — é onde o custo mora).</summary>
    Correction,

    /// <summary>Análise de fraquezas sobre o histórico de erros.</summary>
    Analysis,
}
