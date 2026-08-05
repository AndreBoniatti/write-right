namespace WriteRight.Api.Llm;

/// <summary>
/// A API respondeu (e cobrou), mas a resposta não virou resultado utilizável —
/// recusa do modelo, JSON truncado por bater no teto de tokens, schema inesperado.
///
/// Existe por um motivo só: <b>carregar o <see cref="Usage"/> para fora</b>. Sem isto
/// a exceção subiria crua e o gasto sumiria do registro — e o caso mais caro é
/// justamente este, porque estourar o teto de saída significa ter pago pelos tokens
/// todos antes de o JSON ficar impossível de ler.
///
/// Não é lançada quando a chamada falha ANTES de completar (rede, 429, 5xx): aí não
/// houve cobrança e não há consumo a registrar.
/// </summary>
public sealed class LlmCallFailedException : Exception
{
    public LlmCallFailedException(LlmUsage usage, string message, Exception inner)
        : base(message, inner)
    {
        Usage = usage;
    }

    /// <summary>O que a chamada consumiu antes de falhar. Já foi cobrado.</summary>
    public LlmUsage Usage { get; }
}
