namespace WriteRight.Api.Llm;

/// <summary>
/// Consumo de UMA chamada, como a API reportou. São os quatro baldes de token da
/// Anthropic — eles não se sobrepõem: <see cref="InputTokens"/> já é só o resto
/// NÃO cacheado, então somar os quatro não conta nada duas vezes.
///
/// Token é o fato bruto e imutável; custo é derivado (preço muda). Por isso o que
/// se guarda é isto, e o custo vai junto só como retrato do momento — ver
/// <see cref="LlmPricing"/>.
/// </summary>
public sealed record LlmUsage(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens,
    long CacheReadTokens)
{
    /// <summary>Fallback pra resposta sem bloco de uso — nunca deveria acontecer, mas não vale derrubar a prática por isso.</summary>
    public static LlmUsage Unknown(string model) => new(model, 0, 0, 0, 0);
}

/// <summary>
/// O resultado da IA junto do que ele custou.
///
/// O provider devolve o par em vez de gravar sozinho de propósito: quem sabe a
/// qual prática (ou análise) a chamada pertence é o serviço, não o provider — e
/// manter o provider sem <c>DbContext</c> é o que deixa ele ser trocado por stub
/// nos testes sem arrastar banco junto.
/// </summary>
public sealed record LlmResult<T>(T Value, LlmUsage Usage);
