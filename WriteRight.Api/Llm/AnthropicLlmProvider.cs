using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;

namespace WriteRight.Api.Llm;

/// <summary>
/// Implementação Anthropic da <see cref="ILlmProvider"/> — chama o Claude com
/// structured output tanto na geração quanto na correção.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly LlmOptions _options;

    public AnthropicLlmProvider(IOptions<LlmOptions> options)
    {
        _options = options.Value;
    }

    public async Task<GeneratedExercise> GenerateExerciseAsync(
        ExerciseGenerationRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.GenerationModel,
            MaxTokens = 2000,
            System = GenerationPrompt.BuildSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = GenerationPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = GenerationPrompt.BuildResultSchema() },
            },
        });

        var json = FirstText(response);
        var text = JsonSerializer.Deserialize<GeneratedText>(json, JsonOpts)?.Text
            ?? throw new InvalidOperationException("Falha ao desserializar o texto gerado.");

        return new GeneratedExercise(
            request.SourceLanguage, request.TargetLanguage, text.Trim(), request.Level, request.Theme);
    }

    public async Task<CorrectionResult> CorrectAsync(
        CorrectionRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _options.CorrectionModel,
            MaxTokens = 16000,
            System = CorrectionPrompt.BuildSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = CorrectionPrompt.BuildUserMessage(request) }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = CorrectionPrompt.BuildResultSchema() },
            },
        });

        var json = FirstText(response);
        return JsonSerializer.Deserialize<CorrectionResult>(json, JsonOpts)
            ?? throw new InvalidOperationException("Falha ao desserializar a correção da IA.");
    }

    /// <summary>Extrai o JSON do primeiro bloco de texto (structured output).</summary>
    private static string FirstText(Message response)
    {
        var text = response.Content.Select(b => b.Value).OfType<TextBlock>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(
                $"A IA não devolveu texto (possível recusa). StopReason: {response.StopReason}");
        return text;
    }

    /// <summary>Cria o cliente Anthropic com a key configurada (validação num lugar só).</summary>
    private AnthropicClient CreateClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException(
                "API key da Anthropic não configurada. Rode no projeto Api: " +
                "dotnet user-secrets set \"Llm:ApiKey\" \"sk-ant-...\"");

        return new AnthropicClient { ApiKey = _options.ApiKey };
    }

    /// <summary>Forma do JSON de geração (só o texto).</summary>
    private sealed record GeneratedText(string Text);
}
