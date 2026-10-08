using WriteRight.Api.Data;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Cards;

/// <summary>
/// O portão entre a proposta do modelo e o card. Cada regra aqui existe por um card
/// ruim de verdade — os exemplos são do deck real e da avaliação de 2026-10-07. A
/// regra da casa: na dúvida mecânica, o card não nasce.
/// </summary>
public class CardGateTests
{
    private static readonly ExerciseAttempt Practice = new()
    {
        Status = PracticeStatus.Completed,
        SourceLanguage = Language.Portuguese,
        TargetLanguage = Language.English,
        SourceText = "A arquitetura apresenta características únicas. Ela defende que a pronúncia correta "
                   + "aproxima o estudante da cultura verdadeira. Voltamos para casa e preparamos o jantar.",
        CorrectedText = "The architecture has unique characteristics. She argues that correct pronunciation "
                      + "brings the student closer to the true culture. We went back home and prepared dinner.",
    };

    private static readonly IReadOnlyList<ExerciseError> Sent =
    [
        Error("unique aspects", "unique characteristics"),
        Error("defends", "argues"),
        Error("connect the student with", "brings the student closer to"),
        Error("turned back to home", "went back home"),
    ];

    private static ExerciseError Error(string original, string correction) => new()
    {
        Category = ErrorCategory.WordChoice,
        Severity = ErrorSeverity.Understandable,
        Original = original,
        Correction = correction,
    };

    private static CardProposal Card(int error, string answer, string hint, params string[] alsoCorrect) =>
        new(error, "item", CardDecisionKind.Card, answer, hint, alsoCorrect);

    private static GatedCard? Check(CardProposal proposal) => CardGate.Check(Practice, Sent, proposal);

    [Fact]
    public void Accepts_a_well_formed_card()
    {
        var card = Check(Card(2, "argues", "defende", "maintains", "contends"));

        Assert.NotNull(card);
        Assert.Equal("She ___ that correct pronunciation brings the student closer to the true culture.", card.Prompt);
        Assert.Equal("argues", card.Answer);
        Assert.Equal("defende", card.Hint);
        Assert.Equal(["maintains", "contends"], card.Alternatives);
        Assert.Same(Sent[1], card.Source);
    }

    /// <summary>Só o veredito Card vira card — o resto é justamente o que o passo existe pra barrar.</summary>
    [Theory]
    [InlineData(CardDecisionKind.Grammar)]
    [InlineData(CardDecisionKind.Acceptable)]
    [InlineData(CardDecisionKind.Ambiguous)]
    [InlineData(CardDecisionKind.Other)]
    public void Rejects_every_verdict_but_card(CardDecisionKind verdict)
    {
        Assert.Null(Check(new CardProposal(2, "item", verdict, "argues", "defende", [])));
    }

    /// <summary>O erro é citado pela posição; posição que não existe não aponta pra erro nenhum.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Rejects_an_error_position_out_of_range(int position)
    {
        Assert.Null(Check(Card(position, "argues", "defende")));
    }

    [Theory]
    [InlineData("", "defende")]
    [InlineData("argues", "")]
    [InlineData("   ", "defende")]
    public void Rejects_an_empty_answer_or_hint(string answer, string hint)
    {
        Assert.Null(Check(Card(2, answer, hint)));
    }

    /// <summary>
    /// Card real do deck antigo: deixado em branco toda vez. Cinco palavras não são
    /// um item, são um pedaço de frase que só se acerta decorando a string.
    /// </summary>
    [Fact]
    public void Rejects_an_answer_longer_than_three_words()
    {
        Assert.Null(Check(Card(3, "brings the student closer to", "aproxima o estudante da")));
    }

    /// <summary>Dica fora do texto de origem foi inventada — e pode não apontar pra essa lacuna.</summary>
    [Fact]
    public void Rejects_a_hint_that_is_not_in_the_source_text()
    {
        Assert.Null(Check(Card(2, "argues", "argumenta")));
    }

    [Fact]
    public void Matches_the_hint_ignoring_case()
    {
        Assert.NotNull(Check(Card(2, "argues", "Defende")));
    }

    /// <summary>"característica" está DENTRO de "características", mas não está no texto como palavra.</summary>
    [Fact]
    public void Rejects_a_hint_found_only_inside_another_word()
    {
        Assert.Null(Check(Card(1, "unique characteristics", "característica")));
    }

    /// <summary>Sem a resposta literal no texto corrigido não há lacuna pra montar.</summary>
    [Fact]
    public void Rejects_an_answer_that_is_not_in_the_corrected_text()
    {
        Assert.Null(Check(Card(2, "claims", "defende")));
    }

    /// <summary>
    /// Card real do deck antigo: "unique aspects" é inglês perfeito, e o card o
    /// reprovava. Se a lista de aceitas contém o que o aluno escreveu, o próprio
    /// modelo admitiu que ele estava certo — card que se contradiz não nasce.
    /// </summary>
    [Fact]
    public void Rejects_a_card_whose_alternatives_include_what_the_student_wrote()
    {
        Assert.Null(Check(Card(1, "unique characteristics", "características únicas", "Unique aspects")));
    }

    [Fact]
    public void Rejects_a_card_whose_answer_is_what_the_student_wrote()
    {
        var sent = new[] { Error("argues", "argues") };
        Assert.Null(CardGate.Check(Practice, sent, Card(1, "argues", "defende")));
    }

    /// <summary>
    /// Escreveu "she argues that" e errou outra coisa em volta: ele SABIA "argues".
    /// Um card de "argues" o reprovaria por algo que ele acertou.
    /// </summary>
    [Fact]
    public void Rejects_a_card_whose_answer_the_student_already_wrote_inside_a_longer_span()
    {
        var sent = new[] { Error("She argues that", "She argues") };
        Assert.Null(CardGate.Check(Practice, sent, Card(1, "argues", "defende")));
    }

    [Fact]
    public void Rejects_a_card_whose_alternative_the_student_already_wrote_inside_a_longer_span()
    {
        var sent = new[] { Error("she maintains that", "she argues that") };
        Assert.Null(CardGate.Check(Practice, sent, Card(1, "argues", "defende", "maintains")));
    }

    /// <summary>Conter por palavra, não por letra: "argue" escrito não "contém" a resposta "argues".</summary>
    [Fact]
    public void Containment_is_by_whole_words()
    {
        var sent = new[] { Error("argue", "argues") };
        Assert.NotNull(CardGate.Check(Practice, sent, Card(1, "argues", "defende")));
    }

    /// <summary>
    /// Caso da avaliação: "went back home" passou pelo modelo com oito alternativas.
    /// Lista desse tamanho é ambiguidade com outro nome.
    /// </summary>
    [Fact]
    public void Rejects_more_than_four_alternatives()
    {
        Assert.Null(Check(Card(4, "went back home", "Voltamos para casa",
            "came back home", "returned home", "went home", "came home", "got back home")));
    }

    [Fact]
    public void Accepts_exactly_four_alternatives()
    {
        Assert.NotNull(Check(Card(4, "went back home", "Voltamos para casa",
            "came back home", "returned home", "went home", "got back home")));
    }

    /// <summary>
    /// A contagem é feita DEPOIS da limpeza: repetição, vazio e a própria resposta
    /// não são alternativas, e não podem empurrar um card bom pra fora do limite.
    /// </summary>
    [Fact]
    public void Cleans_the_alternatives_before_counting_them()
    {
        var card = Check(Card(2, "argues", "defende",
            " maintains ", "Maintains", "", "argues", "contends", "holds", "claims"));

        Assert.NotNull(card);
        Assert.Equal(["maintains", "contends", "holds", "claims"], card.Alternatives);
    }
}
