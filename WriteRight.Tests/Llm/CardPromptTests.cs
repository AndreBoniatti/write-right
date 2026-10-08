using System.Text.Json;
using WriteRight.Api.Llm;
using WriteRight.Shared;
using WriteRight.Shared.Cards;

namespace WriteRight.Tests.Llm;

/// <summary>
/// O contrato do passo de desenho de cards: o schema que o modelo preenche e a
/// numeração dos erros, que é o elo entre a resposta do modelo e o portão.
/// </summary>
public class CardPromptTests
{
    private static readonly JsonElement Schema =
        JsonSerializer.SerializeToElement(CardPrompt.BuildResultSchema());

    private static JsonElement Decision() =>
        Schema.GetProperty("properties").GetProperty("decisions").GetProperty("items");

    [Fact]
    public void Top_level_requires_the_decisions()
    {
        Assert.False(Schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["decisions"], Schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public void A_decision_requires_every_field()
    {
        var required = Decision().GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal(["error", "item", "verdict", "answer", "hint", "alsoCorrect"], required);
        Assert.False(Decision().GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Verdict_enum_lists_every_CardDecisionKind()
    {
        var values = Decision().GetProperty("properties").GetProperty("verdict").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!);

        Assert.Equal(Enum.GetNames<CardDecisionKind>(), values);
    }

    /// <summary>
    /// A ordem medida: o modelo escreve da esquerda pra direita, então o veredito vem
    /// ANTES do recorte — ele decide SE é card antes de desenhar o card. Inverter não
    /// quebra nada visível; muda só o comportamento que foi medido.
    /// </summary>
    [Fact]
    public void The_verdict_comes_before_the_card_fields()
    {
        var order = Decision().GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        Assert.True(order.IndexOf("verdict") < order.IndexOf("answer"));
        Assert.True(order.IndexOf("verdict") < order.IndexOf("hint"));
        Assert.True(order.IndexOf("verdict") < order.IndexOf("alsoCorrect"));
    }

    /// <summary>Veredito novo no enum sem explicação no prompt seria um veredito que o modelo nunca usa direito.</summary>
    [Fact]
    public void System_prompt_explains_every_verdict()
    {
        var prompt = CardPrompt.BuildSystemPrompt();

        foreach (var verdict in Enum.GetNames<CardDecisionKind>())
            Assert.Contains($"- {verdict}:", prompt);
    }

    /// <summary>
    /// Os erros saem numerados a partir de 1, na ordem recebida — é esse número que o
    /// modelo devolve e que o portão usa pra achar o erro. Começar do 0, ou reordenar,
    /// casaria cada card com o erro do vizinho.
    /// </summary>
    [Fact]
    public void User_message_numbers_the_errors_from_one_in_order()
    {
        var message = CardPrompt.BuildUserMessage(new CardDesignRequest(
            Language.Portuguese, Language.English,
            SourceText: "Texto de origem.",
            UserTranslation: "Student text.",
            CorrectedText: "Corrected text.",
            Errors: [new("colored fronts", "colorful façades"), new("have", "has")]));

        Assert.Contains("Texto de origem.", message);
        Assert.Contains("Student text.", message);
        Assert.Contains("Corrected text.", message);
        Assert.Contains("1. \"colored fronts\" → \"colorful façades\"", message);
        Assert.Contains("2. \"have\" → \"has\"", message);
        Assert.True(message.IndexOf("1. ", StringComparison.Ordinal) < message.IndexOf("2. ", StringComparison.Ordinal));
    }
}
