using WriteRight.Shared.Cards;

namespace WriteRight.Tests.Cards;

/// <summary>
/// A comparação da resposta digitada. Existe por causa dos cards longos — 12 dos
/// primeiros 76 têm 4+ palavras — em que exigir string idêntica transformaria
/// acerto em erro e envenenaria a nota que alimenta o intervalo.
/// </summary>
public class AnswerMatchTests
{
    [Fact]
    public void Identical_answers_are_correct()
    {
        Assert.Equal(CardVerdict.Correct, AnswerMatch.Check("colorful flowers", "colorful flowers"));
    }

    [Theory]
    [InlineData("Colorful Flowers", "colorful flowers")]         // caixa
    [InlineData("  colorful   flowers  ", "colorful flowers")]   // espaços
    [InlineData("colorful flowers.", "colorful flowers")]        // pontuação de borda
    [InlineData("colorful façades", "colorful facades")]         // acento
    [InlineData("it’s raining", "it's raining")]                 // apóstrofo tipográfico
    public void Normalization_absorbs_what_is_not_knowledge(string typed, string expected)
    {
        Assert.Equal(CardVerdict.Correct, AnswerMatch.Check(typed, expected));
    }

    /// <summary>
    /// Apóstrofo e hífen SOBREVIVEM à normalização: "its" e "it's" são coisas
    /// diferentes, e essa diferença é exatamente o tipo de erro que o app corrige.
    /// </summary>
    [Fact]
    public void Apostrophe_is_meaningful_and_survives_normalization()
    {
        Assert.Equal("it's raining", AnswerMatch.Normalize("It’s raining!"));
        Assert.Equal("high-quality camera", AnswerMatch.Normalize("high-quality camera"));
    }

    [Theory]
    [InlineData("colorful flower", "colorful flowers")]   // faltou o plural
    [InlineData("masterring", "mastering")]               // dedo escorregou
    public void Small_slips_are_near_misses(string typed, string expected)
    {
        Assert.Equal(CardVerdict.NearMiss, AnswerMatch.Check(typed, expected));
    }

    [Theory]
    [InlineData("drink a coffee", "have a coffee")]
    [InlineData("dominate", "mastering")]
    [InlineData("windows of wood", "wooden windows")]
    public void A_different_answer_is_wrong(string typed, string expected)
    {
        Assert.Equal(CardVerdict.Wrong, AnswerMatch.Check(typed, expected));
    }

    /// <summary>
    /// Resposta vazia é erro, nunca "quase" — senão desistir do card e apertar
    /// enter viraria meio acerto.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_answer_is_always_wrong(string typed)
    {
        Assert.Equal(CardVerdict.Wrong, AnswerMatch.Check(typed, "a"));
    }

    /// <summary>
    /// O "quase" conta por PALAVRA, não pela string inteira. Em distância de
    /// caracteres "closer than" e "closer to" ficam a 3 edições — passariam por
    /// engano de digitação num trecho longo, quando são erro de preposição, que é
    /// justamente o que o app corrige.
    /// </summary>
    [Fact]
    public void A_wrong_word_is_wrong_even_when_the_phrase_is_long()
    {
        Assert.Equal(CardVerdict.Wrong,
            AnswerMatch.Check("brings the student closer than", "brings the student closer to"));
        Assert.Equal(CardVerdict.NearMiss,
            AnswerMatch.Check("brings the student close to", "brings the student closer to"));
    }

    /// <summary>Dois deslizes deixam de ser deslize.</summary>
    [Fact]
    public void More_than_one_slipped_word_is_wrong()
    {
        Assert.Equal(CardVerdict.Wrong,
            AnswerMatch.Check("colorfull flower", "colorful flowers"));
    }

    /// <summary>
    /// Palavra a mais ou a menos não é digitação. Vale inclusive pro artigo
    /// esquecido — "Artigo" é uma categoria da taxonomia, não um detalhe.
    /// </summary>
    [Fact]
    public void A_missing_word_is_wrong()
    {
        Assert.Equal(CardVerdict.Wrong,
            AnswerMatch.Check("high-quality camera", "a high-quality camera"));
    }
}
