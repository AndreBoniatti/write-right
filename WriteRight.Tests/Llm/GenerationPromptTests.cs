using System.Text.Json;
using WriteRight.Api.Llm;
using WriteRight.Shared;
using WriteRight.Shared.Exercises;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Llm;

/// <summary>
/// O gancho adaptativo vive no prompt de geração: com <c>FocusCategories</c>, o
/// texto pedido ao modelo precisa injetar essas fraquezas; sem elas, nenhum bloco
/// de foco. É a diferença entre "exercício qualquer" e "exercício que mira o que o
/// aluno erra" — o coração do produto.
/// </summary>
public class GenerationPromptTests
{
    private static ExerciseGenerationRequest Request(IReadOnlyList<ErrorCategory>? focus) =>
        new(Language.Portuguese, Language.English, 60, CefrLevel.B1, Theme: null, FocusCategories: focus);

    [Fact]
    public void User_message_injects_focus_categories_when_present()
    {
        var focus = new[] { ErrorCategory.Preposition, ErrorCategory.VerbTense };
        var msg = GenerationPrompt.BuildUserMessage(Request(focus));

        Assert.Contains("IMPORTANTE", msg);   // o bloco de foco
        foreach (var cat in focus)
            Assert.Contains(ErrorCatalog.Info(cat).LabelPt, msg);
    }

    [Fact]
    public void User_message_has_no_focus_block_when_categories_absent()
    {
        Assert.DoesNotContain("IMPORTANTE", GenerationPrompt.BuildUserMessage(Request(null)));
        Assert.DoesNotContain("IMPORTANTE", GenerationPrompt.BuildUserMessage(Request(Array.Empty<ErrorCategory>())));
    }

    [Fact]
    public void Schema_requires_the_text_field()
    {
        var schema = JsonSerializer.SerializeToElement(GenerationPrompt.BuildResultSchema());

        var required = schema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        Assert.Equal(new[] { "text" }, required);
        Assert.True(schema.GetProperty("properties").TryGetProperty("text", out _));
    }
}
