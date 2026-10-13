using WriteRight.Shared.Corrections;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Corrections;

/// <summary>
/// A marcação da correção no texto. Os casos vêm de práticas reais — cada um é um
/// jeito que o corretor tem de descrever os erros que quebraria a montagem ingênua
/// (trocar erro a erro): sobreposição, original repetido, original meio corrigido,
/// mudança sem erro.
/// </summary>
public class CorrectionMarkupTests
{
    [Fact]
    public void Identical_texts_have_no_changes()
    {
        var markup = CorrectionMarkup.Build("I like it.", "I like it.", []);

        Assert.Equal([new UnchangedText("I like it.")], markup.Segments);
        Assert.Empty(markup.UnlocatedErrors);
    }

    [Fact]
    public void Each_change_carries_the_errors_that_touch_it()
    {
        var markup = CorrectionMarkup.Build(
            "Repair a car is more expensive than repair a bike, but also requires proper tools.",
            "Repairing a car is more expensive than repairing a bike, but both require proper tools.",
            [
                Error("Repair a car", "Repairing a car"),
                Error("repair a bike", "repairing a bike"),
                Error("but also requires", "but both require"),
            ]);

        Assert.Equal(
            "[Repair→Repairing]{0} a car is more expensive than [repair→repairing]{1} a bike, but [also requires→both require]{2} proper tools.",
            Render(markup));
    }

    /// <summary>
    /// Um erro grande com três menores dentro. Trocando erro a erro, o mesmo pedaço
    /// apareceria quatro vezes; pela diferença, é uma mudança só com as quatro
    /// explicações — e a inserção logo depois fica com as três que a alcançam.
    /// </summary>
    [Fact]
    public void Overlapping_errors_share_the_change_instead_of_repeating_text()
    {
        var markup = CorrectionMarkup.Build(
            "She appreciate a lot this convivency moments.",
            "She really appreciates these moments together.",
            [
                Error("She appreciate", "She appreciates"),
                Error("appreciate a lot this convivency moments", "really appreciates these moments together"),
                Error("this convivency moments", "these moments"),
                Error("convivency moments", "moments together"),
            ]);

        Assert.Equal(
            "She [appreciate a lot this convivency→really appreciates these]{0,1,2,3} moments [→together]{1,2,3}.",
            Render(markup));
    }

    /// <summary>
    /// "stayed" aparece duas vezes e as duas mudam. O erro "stayed → left" é o da
    /// segunda — a única cuja mudança traz o "left".
    /// </summary>
    [Fact]
    public void A_repeated_original_goes_to_the_occurrence_whose_change_holds_the_correction()
    {
        var markup = CorrectionMarkup.Build(
            "Yesterday I stayed the whole afternoon looking for the keys that I have stayed in the car.",
            "Yesterday I spent the whole afternoon looking for the keys that I had left in the car.",
            [
                Error("stayed the whole afternoon", "spent the whole afternoon"),
                Error("have stayed", "had left"),
                Error("stayed", "left"),
            ]);

        Assert.Equal(
            "Yesterday I [stayed→spent]{0} the whole afternoon looking for the keys that I [have stayed→had left]{1,2} in the car.",
            Render(markup));
    }

    /// <summary>
    /// O modelo escreveu o original com "space", mas o aluno escreveu "spacial" — o
    /// trecho não existe na tradução. A correção existe no corrigido, e é por ela que
    /// o erro acha a mudança "on → at" que ele explica.
    /// </summary>
    [Fact]
    public void An_original_written_half_corrected_is_found_through_its_correction()
    {
        var markup = CorrectionMarkup.Build(
            "They trained on the spacial center.",
            "They trained at the space center.",
            [
                Error("spacial", "space"),
                Error("on the space center", "at the space center"),
            ]);

        Assert.Equal("They trained [on→at]{1} the [spacial→space]{0,1} center.", Render(markup));
        Assert.Empty(markup.UnlocatedErrors);
    }

    [Fact]
    public void A_change_no_error_explains_carries_no_error()
    {
        var markup = CorrectionMarkup.Build("We went to the moon.", "We went to the Moon.", []);

        Assert.Equal("We went to the [moon→Moon]{}.", Render(markup));
    }

    [Fact]
    public void An_error_found_nowhere_is_reported_as_unlocated()
    {
        var markup = CorrectionMarkup.Build(
            "I has a dog.",
            "I have a dog.",
            [Error("has", "have"), Error("they goes", "they go")]);

        Assert.Equal("I [has→have]{0} a dog.", Render(markup));
        Assert.Equal([1], markup.UnlocatedErrors);
    }

    /// <summary>Original vazio não pode travar a busca (IndexOf("") acha tudo).</summary>
    [Fact]
    public void An_empty_original_is_unlocated_instead_of_matching_everywhere()
    {
        var markup = CorrectionMarkup.Build("I has a dog.", "I have a dog.", [Error("", "")]);

        Assert.Equal([0], markup.UnlocatedErrors);
    }

    /// <summary>
    /// No empate da subsequência comum, apagar antes de inserir junta a troca num
    /// trecho só, em vez de "+going to −go to".
    /// </summary>
    [Fact]
    public void A_rewritten_phrase_comes_out_as_one_change()
    {
        var markup = CorrectionMarkup.Build(
            "They studied before to go to the moon.",
            "They studied before going to the moon.",
            [Error("before to go", "before going")]);

        Assert.Equal("They studied before [to go→going]{0} to the moon.", Render(markup));
    }

    [Fact]
    public void Contractions_are_one_word()
    {
        var markup = CorrectionMarkup.Build(
            "It is her granddaughter birthday.",
            "It is her granddaughter's birthday.",
            [Error("granddaughter birthday", "granddaughter's birthday")]);

        Assert.Equal("It is her [granddaughter→granddaughter's]{0} birthday.", Render(markup));
    }

    /// <summary>
    /// A palavra removida não tem espaço no corrigido, então quem desenha põe um;
    /// a vírgula removida cola na palavra anterior e não precisa.
    /// </summary>
    [Fact]
    public void Only_a_removed_word_asks_for_a_space_before_it()
    {
        var markup = CorrectionMarkup.Build(
            "I found my apartment keys, and left.",
            "I found my keys and left.",
            []);

        var removals = markup.Segments.OfType<ChangedText>().ToList();

        Assert.Equal("I found my[apartment→]{} keys[,→]{} and left.", Render(markup));
        Assert.True(removals[0].IsWordRemoval);
        Assert.False(removals[1].IsWordRemoval);
    }

    /// <summary>
    /// O texto sem marcação mais o que entrou tem que dar exatamente o corrigido —
    /// espaços duplos e quebras de linha inclusos. É o que garante que a marcação
    /// nunca mostra uma frase que o corretor não escreveu.
    /// </summary>
    [Theory]
    [InlineData("Repair a car is expensive.", "Repairing a car is expensive.")]
    [InlineData("Hello  world.\nI am here.", "Hello world.\nI'm here.")]
    [InlineData("before to go to the moon", "before going to the Moon.")]
    [InlineData("Descobri they are inside.", "I found out they were inside.")]
    [InlineData("", "Something new.")]
    [InlineData("Something old.", "")]
    public void Unchanged_and_inserted_text_rebuild_the_corrected_text(string mine, string corrected)
    {
        var markup = CorrectionMarkup.Build(mine, corrected, []);

        var rebuilt = string.Concat(markup.Segments.Select(s => s switch
        {
            UnchangedText u => u.Text,
            ChangedText c => c.Inserted,
            _ => throw new InvalidOperationException(),
        }));

        Assert.Equal(corrected, rebuilt);
    }

    private static WritingError Error(string original, string correction) =>
        new(ErrorCategory.Other, ErrorSeverity.Understandable, original, correction, "");

    /// <summary>"[removido→inserido]{índices dos erros}" no lugar de cada mudança.</summary>
    private static string Render(CorrectionMarkup markup) =>
        string.Concat(markup.Segments.Select(s => s switch
        {
            UnchangedText u => u.Text,
            ChangedText c => $"[{c.Removed}→{c.Inserted}]{{{string.Join(",", c.Errors)}}}",
            _ => throw new InvalidOperationException(),
        }));
}
