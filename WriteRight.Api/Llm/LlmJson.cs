using System.Text.Json;
using System.Text.Json.Serialization;

namespace WriteRight.Api.Llm;

/// <summary>
/// Opções canônicas de (de)serialização do contrato com a IA. Ficam num lugar só
/// para o <see cref="AnthropicLlmProvider"/> e os testes exercitarem <b>exatamente</b>
/// a mesma desserialização — enums como STRING (o vínculo que sustenta banco, API e wire)
/// e nomes de propriedade case-insensitive.
/// </summary>
internal static class LlmJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
