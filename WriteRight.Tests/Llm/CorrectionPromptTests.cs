using WriteRight.Api.Llm;
using WriteRight.Shared;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Llm;

/// <summary>
/// O system prompt de correção precisa ensinar ao modelo TODAS as categorias
/// (senão a IA não tem como classificar nelas). Trava essa injeção completa e a
/// presença dos dois textos na mensagem do usuário.
/// </summary>
public class CorrectionPromptTests
{
    [Fact]
    public void System_prompt_lists_every_category_identifier()
    {
        var prompt = CorrectionPrompt.BuildSystemPrompt();

        foreach (var category in Enum.GetValues<ErrorCategory>())
            Assert.Contains(category.ToString(), prompt);
    }

    [Fact]
    public void User_message_contains_source_and_translation()
    {
        var req = new CorrectionRequest(
            Language.Portuguese, Language.English,
            SourceText: "Um texto de origem único.",
            UserTranslation: "A unique source text.");

        var msg = CorrectionPrompt.BuildUserMessage(req);

        Assert.Contains("Um texto de origem único.", msg);
        Assert.Contains("A unique source text.", msg);
    }
}
