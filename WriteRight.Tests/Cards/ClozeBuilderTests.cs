using WriteRight.Api.Services;
using WriteRight.Shared.Cards;

namespace WriteRight.Tests.Cards;

/// <summary>
/// A frente do card sai por string matching, sem IA — medido em 99% dos erros de
/// vocabulário reais. O que estes testes protegem é sobretudo o CAMINHO DE FALHA:
/// quando a frase não presta, o card não pode nascer. Um card com frente ruim é
/// pior que card nenhum — seria respondido errado pra sempre e envenenaria as
/// estatísticas que dizem se o agendador funciona.
/// </summary>
public class ClozeBuilderTests
{
    [Fact]
    public void Replaces_the_correction_with_a_blank()
    {
        var cloze = ClozeBuilder.Build(
            "The old buildings have colorful façades and clay roofs.", "colorful façades");

        Assert.Equal("The old buildings have ___ and clay roofs.", cloze);
    }

    [Fact]
    public void Cuts_only_the_sentence_that_contains_the_correction()
    {
        var cloze = ClozeBuilder.Build(
            "She woke up early. Yesterday I spent the whole afternoon looking for my keys. Then I left.",
            "spent the whole afternoon");

        Assert.Equal("Yesterday I ___ looking for my keys.", cloze);
    }

    /// <summary>
    /// O trecho pode vir do meio da frase e aparecer no texto com maiúscula (ou
    /// vice-versa) — é a mesma resposta, e recusar o card por causa disso perderia
    /// material bom.
    /// </summary>
    [Fact]
    public void Matches_ignoring_case()
    {
        var cloze = ClozeBuilder.Build("Mastering grammar takes years of work.", "mastering");

        Assert.Equal("___ grammar takes years of work.", cloze);
    }

    [Fact]
    public void Returns_null_when_the_correction_is_not_in_the_text()
    {
        // O caso real dos 76: "helpful worker" não aparecia literal no texto corrigido.
        Assert.Null(ClozeBuilder.Build("She is a hard worker and arrives early.", "helpful worker"));
    }

    /// <summary>
    /// Sem contexto em volta, a lacuna não tem resposta única — "___." aceita
    /// qualquer coisa. Melhor não ter o card.
    /// </summary>
    [Theory]
    [InlineData("Colorful flowers.", "colorful flowers")]
    [InlineData("He left.", "left")]
    public void Returns_null_when_the_sentence_has_no_context_left(string text, string correction)
    {
        Assert.Null(ClozeBuilder.Build(text, correction));
    }

    /// <summary>
    /// Só a ocorrência que o erro aponta vira lacuna. Se a expressão se repete, a
    /// segunda fica à vista — e aí serve de pista legítima, não de segundo buraco.
    /// </summary>
    [Fact]
    public void Blanks_only_the_first_occurrence()
    {
        var cloze = ClozeBuilder.Build(
            "I have a coffee when she has a coffee too.", "a coffee");

        Assert.Equal("I have ___ when she has a coffee too.", cloze);
    }

    [Theory]
    [InlineData("", "algo")]
    [InlineData("Um texto qualquer.", "")]
    [InlineData("Um texto qualquer.", "   ")]
    public void Returns_null_for_empty_input(string text, string correction)
    {
        Assert.Null(ClozeBuilder.Build(text, correction));
    }

    /// <summary>
    /// O marcador é escrito aqui e lido pelo cliente (que desenha a lacuna com a
    /// dica dentro). Este teste amarra as duas pontas: o que o servidor produz é o
    /// que o <see cref="Cloze.Split"/> consegue partir.
    /// </summary>
    [Fact]
    public void What_the_builder_writes_is_what_the_client_can_split()
    {
        var cloze = ClozeBuilder.Build(
            "The old buildings have colorful façades and clay roofs.", "colorful façades")!;

        var partes = Cloze.Split(cloze);

        Assert.NotNull(partes);
        Assert.Equal("The old buildings have ", partes!.Value.Before);
        Assert.Equal(" and clay roofs.", partes.Value.After);
    }

    [Fact]
    public void Splitting_a_prompt_without_a_blank_returns_null()
    {
        Assert.Null(Cloze.Split("Uma frase sem lacuna nenhuma."));
        Assert.Null(Cloze.Split(""));
    }
}
