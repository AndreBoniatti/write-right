namespace WriteRight.Shared.Usage;

/// <summary>
/// Consumo agregado de uma operação num modelo. A chave é o par
/// (operação, modelo): trocar o modelo de correção por config não deve somar maçã
/// com laranja no mesmo balde.
/// </summary>
/// <param name="Calls">Quantas chamadas.</param>
/// <param name="InputTokens">Tokens de entrada NÃO cacheados (o resto vem nos campos de cache).</param>
/// <param name="CacheWriteTokens">Tokens gravados no cache de prompt (custam ~1,25× a entrada).</param>
/// <param name="CacheReadTokens">Tokens servidos do cache (custam ~0,1× a entrada).</param>
/// <param name="CostUsd">Soma dos custos conhecidos deste balde.</param>
public sealed record UsageByOperation(
    LlmOperation Operation,
    string Model,
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens,
    long CacheReadTokens,
    decimal CostUsd);

/// <summary>
/// Relatório de consumo. Existe pra responder UMA pergunta com dado real em vez de
/// estimativa: quanto custa, de fato, uma prática e uma análise.
/// </summary>
/// <param name="UnpricedCalls">
/// Chamadas cujo modelo não estava na tabela de preços — os tokens foram gravados,
/// o custo não pôde ser calculado. Se isto for &gt; 0, o total está SUBESTIMADO e a
/// tabela de preços precisa de uma entrada nova.
/// </param>
/// <param name="AvgCostPerPracticeUsd">
/// Custo atribuído a práticas ÷ práticas concluídas. Inclui de propósito a geração
/// de práticas abandonadas: você pagou por elas, então elas encarecem o custo real
/// de cada prática que chega ao fim.
/// </param>
public sealed record UsageReport(
    int TotalCalls,
    decimal TotalCostUsd,
    int UnpricedCalls,
    IReadOnlyList<UsageByOperation> ByOperation,
    int CompletedPractices,
    decimal? AvgCostPerPracticeUsd,
    int AnalysisCalls,
    decimal? AvgCostPerAnalysisUsd,
    DateTimeOffset? FirstCallAt,
    DateTimeOffset? LastCallAt);
