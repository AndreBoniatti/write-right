using WriteRight.Shared;

namespace WriteRight.Tests.Shared;

/// <summary>
/// Rótulos de dificuldade dos níveis CEFR — garante que todo nível tem um rótulo
/// PT de verdade (não cai no fallback do código), pra a UI nunca mostrar só "B2".
/// </summary>
public class CefrLevelsTests
{
    public static IEnumerable<object[]> AllLevels() =>
        Enum.GetValues<CefrLevel>().Select(l => new object[] { l });

    [Theory]
    [MemberData(nameof(AllLevels))]
    public void Every_level_has_a_real_difficulty_label(CefrLevel level)
    {
        var label = CefrLevels.Label(level);

        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.NotEqual(level.ToString(), label); // rótulo de verdade, não o código de fallback
    }

    [Fact]
    public void Display_combines_code_and_label()
    {
        Assert.Equal("B1 · Intermediário", CefrLevels.Display(CefrLevel.B1));
    }
}
