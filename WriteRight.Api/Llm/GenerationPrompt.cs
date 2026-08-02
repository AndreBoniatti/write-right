using System.Text;
using System.Text.Json;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Llm;

/// <summary>
/// Constrói o prompt de geração de exercício e o schema do structured output.
/// O gancho adaptativo vive aqui: quando há FocusCategories, o texto é pedido de
/// forma que a tradução force o aluno a praticar suas fraquezas.
/// </summary>
internal static class GenerationPrompt
{
    public static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você gera textos curtos para um estudante de idiomas traduzir.");
        sb.AppendLine("Escreva um texto natural, coeso e original — sem título, sem marcações, sem aspas.");
        sb.AppendLine("O estudante vai LER o texto no idioma de origem e TRADUZIR para o idioma-alvo.");
        sb.AppendLine();
        sb.AppendLine("O texto é a REFERÊNCIA que o aluno lê: precisa ser impecável no idioma de origem.");
        sb.AppendLine("Regras de qualidade (obrigatórias):");
        sb.AppendLine("- Gramática correta e ortografia padrão (culta), sem registro telegráfico ou informal.");
        sb.AppendLine("- Comece cada frase com letra MAIÚSCULA e use pontuação correta.");
        sb.AppendLine("- Capitalize nomes próprios e topônimos (ex.: São Paulo, não são paulo).");
        sb.AppendLine("- NÃO omita artigos, preposições ou palavras funcionais (ex.: 'para o parque', não 'para parque').");
        sb.AppendLine("- Releia mentalmente: cada frase deve fazer sentido completo e soar como um falante nativo culto.");
        sb.AppendLine();
        sb.AppendLine("NÃO inclua a tradução, dicas, listas ou qualquer meta-comentário: apenas o texto.");
        sb.AppendLine("Responda no formato estruturado, com o texto no campo 'text'.");
        return sb.ToString();
    }

    public static string BuildUserMessage(ExerciseGenerationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Idioma do texto (origem): {LlmText.LanguageName(request.SourceLanguage)}");
        sb.AppendLine($"O estudante vai traduzir para: {LlmText.LanguageName(request.TargetLanguage)}");
        sb.AppendLine($"Tamanho: aproximadamente {request.WordCount} palavras.");
        sb.AppendLine($"Nível (CEFR): {request.Level}.");

        // Tema do usuário é fronteira; assunto sorteado é trampolim. A diferença de
        // palavra importa: "Tema: X" o modelo lê como cerca e não sai dali, o que
        // reduziria os textos aos limites literais do catálogo. Quando a escolha foi
        // NOSSA, e não do usuário, ele deve poder derivar. (O fallback antigo, "um
        // tema cotidiano", era o funil que produzia sempre a mesma vinheta doméstica.)
        if (!string.IsNullOrWhiteSpace(request.Theme))
            sb.AppendLine($"Tema: {request.Theme}.");
        else if (request.Variety is { } picked)
            sb.AppendLine($"Ponto de partida: {picked.Domain} — sinta-se livre para derivar " +
                          "para assuntos vizinhos ou para um recorte inesperado dele.");
        else
            sb.AppendLine("Tema: escolha um assunto específico e concreto.");

        if (request.Variety is { } variety)
        {
            sb.AppendLine();
            sb.AppendLine("FORMA (siga à risca — estes eixos mudam a cada exercício de propósito):");
            sb.AppendLine($"- Tempo verbal predominante: {variety.Tense}.");
            sb.AppendLine($"- Registro: {variety.Register}.");
            sb.AppendLine($"- Ponto de vista: {variety.PointOfView}.");
            if (variety.CharacterInitial is { } initial)
                sb.AppendLine($"- O nome do personagem deve começar com a letra {initial}.");
        }

        if (request.FocusCategories is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("IMPORTANTE — componha o texto de forma que a TRADUÇÃO para o idioma-alvo");
            sb.AppendLine("naturalmente exija que o estudante lide com estes tipos de dificuldade");
            sb.AppendLine("(sem marcá-los no texto; apenas faça-os surgir de forma natural):");
            foreach (var cat in request.FocusCategories)
            {
                var info = ErrorCatalog.Info(cat);
                sb.AppendLine($"- {info.LabelPt}: {info.Description}");
            }
        }
        return sb.ToString();
    }

    public static Dictionary<string, JsonElement> BuildResultSchema() => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new { text = new { type = "string" } }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "text" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };
}
