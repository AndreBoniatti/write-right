namespace WriteRight.Api.Llm;

/// <summary>
/// Configuração do provedor de IA. A <see cref="ApiKey"/> NUNCA vai no código nem no
/// appsettings versionado — vem de user-secrets (dev) ou variável de ambiente.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>API key da Anthropic. Origem: user-secrets "Llm:ApiKey" ou env "Llm__ApiKey".</summary>
    public string? ApiKey { get; set; }

    /// <summary>Modelo pra geração (barato).</summary>
    public string GenerationModel { get; set; } = "claude-haiku-4-5";

    /// <summary>Modelo pra correção (melhor — é onde a qualidade importa).</summary>
    public string CorrectionModel { get; set; } = "claude-sonnet-5";

    /// <summary>
    /// Modelo pra análise de fraquezas: olha o histórico inteiro e precisa achar
    /// estrutura, e modelo fraco aqui devolve o conselho genérico que o desenho todo
    /// tenta evitar. Roda raro (análise persistida), então o custo pesa pouco.
    /// </summary>
    public string AnalysisModel { get; set; } = "claude-sonnet-5";

    /// <summary>
    /// Modelo pro desenho dos cards. O MAIOR dos quatro, de propósito: card errado entra
    /// no SM-2 e é treinado por meses, então é onde precisão vale mais que custo.
    /// </summary>
    public string CardModel { get; set; } = "claude-opus-5-5";

    /// <summary>
    /// Tarifas por modelo, em USD por 1M de tokens. <b>Única</b> fonte de preço — não
    /// há tabela embutida no código: preço muda mais que código, e ter os valores em dois
    /// lugares só cria a dúvida de qual está valendo. Config e não banco porque são
    /// poucos valores, editados raramente, versionados junto do código que os usa.
    ///
    /// Como não há fallback, todo modelo em uso PRECISA estar aqui; quem cobra isso é
    /// o <see cref="LlmOptionsValidator"/>, no startup.
    /// </summary>
    public Dictionary<string, ModelRate> Pricing { get; set; } = new();
}

/// <summary>Tarifa de um modelo, em USD por 1 milhão de tokens.</summary>
public sealed class ModelRate
{
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
}
