using System.Text;
using System.Text.Json;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Llm;

/// <summary>
/// Constrói o prompt de correção e o JSON schema do structured output.
/// Separado do provider pra isolar "o que pedir ao modelo" de "como chamar o SDK".
/// </summary>
internal static class CorrectionPrompt
{
    /// <summary>
    /// System prompt: explica a tarefa, injeta a taxonomia (do <see cref="ErrorCatalog"/>)
    /// e as regras de classificação. As explicações saem em PORTUGUÊS.
    /// </summary>
    public static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um professor de idiomas que corrige traduções de estudantes.");
        sb.AppendLine("O aluno leu um texto no idioma de origem e escreveu uma tradução no idioma-alvo.");
        sb.AppendLine("Sua tarefa: corrigir a tradução e classificar CADA erro.");
        sb.AppendLine();
        sb.AppendLine("REGRAS:");
        sb.AppendLine("- Toda explicação deve ser em PORTUGUÊS do Brasil.");
        sb.AppendLine("- Classifique cada erro em UMA categoria da lista fixa abaixo. Se couber em duas, escolha a MAIS ESPECÍFICA.");
        sb.AppendLine("- Use \"Other\" apenas quando nada mais encaixar.");
        sb.AppendLine("- 'severity': BreaksMeaning (compromete o entendimento), Understandable (dá pra entender mas está errado), Polish (correto, só lapidação).");
        sb.AppendLine("- 'original' = o trecho errado como o aluno escreveu; 'correction' = esse mesmo trecho corrigido.");
        sb.AppendLine("- 'sourcePhrase' = o trecho do TEXTO ORIGINAL (no idioma de origem) que corresponde a este erro,");
        sb.AppendLine("  copiado literalmente. Vira a dica de um card de revisão, então recorte o menor trecho que ainda");
        sb.AppendLine("  identifique a ideia sozinho. String VAZIA quando não houver correspondência (ortografia, pontuação).");
        sb.AppendLine("- 'correctedText' = a tradução inteira, corrigida e natural.");
        sb.AppendLine("- Se a tradução estiver perfeita, devolva 'errors' vazio.");
        sb.AppendLine();
        sb.AppendLine("CATEGORIAS (use exatamente estes identificadores no campo 'category'):");
        foreach (var info in ErrorCatalog.All)
        {
            sb.Append("- ").Append(info.Category).Append(": ")
              .Append(info.Description).Append(" Ex.: ").Append(info.Example).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Mensagem do usuário com os dados concretos do exercício.</summary>
    public static string BuildUserMessage(CorrectionRequest request) =>
        $"Idioma de origem: {LlmText.LanguageName(request.SourceLanguage)}\n" +
        $"Idioma-alvo (o que o aluno escreveu): {LlmText.LanguageName(request.TargetLanguage)}\n\n" +
        $"TEXTO ORIGINAL:\n{request.SourceText}\n\n" +
        $"TRADUÇÃO DO ALUNO:\n{request.UserTranslation}";

    /// <summary>
    /// JSON schema do <see cref="CorrectionResult"/> pro structured output. Os
    /// enums vêm direto do C# (<see cref="Enum.GetNames{T}()"/>) → o schema fica
    /// sempre em sincronia com a taxonomia, sem lista duplicada.
    /// </summary>
    public static Dictionary<string, JsonElement> BuildResultSchema()
    {
        var errorItem = new
        {
            type = "object",
            properties = new
            {
                category = new { type = "string", @enum = Enum.GetNames<ErrorCategory>() },
                severity = new { type = "string", @enum = Enum.GetNames<ErrorSeverity>() },
                original = new { type = "string" },
                correction = new { type = "string" },
                explanation = new { type = "string" },
                sourcePhrase = new { type = "string" },
            },
            // 'sourcePhrase' entra como OBRIGATÓRIO com string vazia permitida, e não
            // como campo opcional: modelo pula campo opcional com frequência, e aí a
            // ausência ficaria ambígua — "não havia correspondência" ou "esqueceu"?
            required = new[] { "category", "severity", "original", "correction", "explanation", "sourcePhrase" },
            additionalProperties = false,
        };

        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                correctedText = new { type = "string" },
                errors = new { type = "array", items = errorItem },
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "correctedText", "errors" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
    }
}
