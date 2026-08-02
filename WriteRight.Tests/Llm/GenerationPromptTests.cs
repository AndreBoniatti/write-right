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

    private static TextVariety Variety(char? initial = 'R') => new(
        "clima, estações do ano e fenômenos naturais",
        "futuro",
        "expositivo (explica um assunto)",
        initial is null ? "sem personagem — o texto trata do assunto em si"
                        : "terceira pessoa, com um personagem nomeado",
        initial);

    private static ExerciseGenerationRequest RequestWith(string? theme, TextVariety? variety) =>
        new(Language.Portuguese, Language.English, 60, CefrLevel.B1, theme, null, variety);

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

    // ── Variedade de forma ───────────────────────────────────────────────────

    [Fact]
    public void User_message_injects_every_variety_axis()
    {
        var variety = Variety();
        var msg = GenerationPrompt.BuildUserMessage(RequestWith(theme: null, variety));

        Assert.Contains(variety.Tense, msg);
        Assert.Contains(variety.Register, msg);
        Assert.Contains(variety.PointOfView, msg);
        Assert.Contains("letra R", msg);
    }

    [Fact]
    public void User_message_omits_the_character_line_when_there_is_no_character()
    {
        var msg = GenerationPrompt.BuildUserMessage(RequestWith(theme: null, Variety(initial: null)));

        Assert.DoesNotContain("letra", msg);
    }

    [Fact]
    public void Users_theme_is_a_boundary_but_the_sorted_one_is_a_starting_point()
    {
        var variety = Variety();

        // Escolha nossa: o modelo pode derivar. Sem isso, os textos ficariam presos
        // ao literal do catálogo e o alcance viraria o tamanho da lista.
        var withoutTheme = GenerationPrompt.BuildUserMessage(RequestWith(theme: null, variety));
        Assert.Contains($"Ponto de partida: {variety.Domain}", withoutTheme);
        Assert.Contains("derivar", withoutTheme);

        // Escolha do usuário: é cerca, e o assunto sorteado não pode atropelá-la.
        var withTheme = GenerationPrompt.BuildUserMessage(RequestWith("viagem de trem", variety));
        Assert.Contains("Tema: viagem de trem", withTheme);
        Assert.DoesNotContain("Ponto de partida", withTheme);
        Assert.DoesNotContain(variety.Domain, withTheme);
    }

    [Fact]
    public void User_message_never_asks_for_an_everyday_theme()
    {
        // O fallback "um tema cotidiano" era o funil que produzia sempre a mesma
        // vinheta doméstica. Se voltar, a repetição volta junto.
        foreach (var msg in new[]
        {
            GenerationPrompt.BuildUserMessage(RequestWith(theme: null, Variety())),
            GenerationPrompt.BuildUserMessage(RequestWith(theme: null, variety: null)),
            GenerationPrompt.BuildUserMessage(Request(null)),
        })
        {
            Assert.DoesNotContain("cotidiano", msg);
        }
    }

    [Fact]
    public void User_message_has_no_form_block_without_variety()
    {
        Assert.DoesNotContain("FORMA", GenerationPrompt.BuildUserMessage(Request(null)));
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
