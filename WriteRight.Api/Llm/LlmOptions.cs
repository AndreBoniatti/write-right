namespace WriteRight.Api.Llm;

/// <summary>
/// Configuração do provedor de IA.
///
/// A <see cref="ApiKey"/> NUNCA vai no código nem no appsettings versionado —
/// vem de user-secrets (dev) ou variável de ambiente (execução):
///   dotnet user-secrets set "Llm:ApiKey" "sk-ant-..."   (no projeto Api)
///
/// Os modelos são configuráveis (split barato/bom decidido no projeto):
/// Haiku gera, Sonnet corrige. Trocáveis por config sem tocar no código.
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
    /// Modelo pra análise de fraquezas. É a tarefa mais analítica do app — a
    /// correção olha um texto, esta olha o histórico inteiro e precisa achar
    /// estrutura. Modelo fraco aqui devolve exatamente o conselho genérico que o
    /// desenho todo tenta evitar. Roda raro (análise persistida), então o custo por
    /// chamada pesa pouco.
    /// </summary>
    public string AnalysisModel { get; set; } = "claude-sonnet-5";

    /// <summary>
    /// Tarifas por modelo, em USD por 1M de tokens. <b>Única</b> fonte de preço — não
    /// há tabela embutida no código, de propósito: preço muda mais que código, e ter
    /// os valores em dois lugares só cria a dúvida de qual está valendo.
    ///
    /// Como não há fallback, todo modelo em uso PRECISA estar aqui; quem cobra isso é
    /// o <see cref="LlmOptionsValidator"/>, no startup.
    ///
    /// Vive em config e não no banco porque isto é <b>configuração</b>, não dado:
    /// poucos valores, editados raramente, e que devem ser versionados junto do código
    /// que os usa. (Preço que você COBRA do usuário é outra coisa — esse sim vai pro
    /// banco quando existir.)
    /// </summary>
    public Dictionary<string, ModelRate> Pricing { get; set; } = new();
}

/// <summary>Tarifa de um modelo, em USD por 1 milhão de tokens.</summary>
public sealed class ModelRate
{
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
}
