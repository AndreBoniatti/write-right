using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Taxonomy;

/// <summary>
/// O <see cref="ErrorCatalog"/> é o ativo central: toda categoria da taxonomia
/// precisa ter metadados (consumidos pela UI e injetados no prompt da IA). Estes
/// testes travam essa completude — adicionar um valor no enum e esquecer o
/// catálogo passa a quebrar o build de testes, não a produção silenciosamente.
/// </summary>
public class ErrorCatalogTests
{
    public static IEnumerable<object[]> AllCategories() =>
        Enum.GetValues<ErrorCategory>().Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void Every_category_has_complete_info(ErrorCategory category)
    {
        var info = ErrorCatalog.Info(category);

        Assert.Equal(category, info.Category);
        Assert.False(string.IsNullOrWhiteSpace(info.LabelPt), "LabelPt vazio");
        Assert.False(string.IsNullOrWhiteSpace(info.Description), "Description vazia");
        Assert.False(string.IsNullOrWhiteSpace(info.Example), "Example vazio");
    }

    [Fact]
    public void All_covers_every_enum_value_exactly_once()
    {
        var catalogued = ErrorCatalog.All.Select(i => i.Category).ToList();
        var enumValues = Enum.GetValues<ErrorCategory>();

        Assert.Equal(enumValues.Length, catalogued.Count);                 // nenhuma faltando
        Assert.Equal(catalogued.Count, catalogued.Distinct().Count());     // nenhuma duplicada
        Assert.Equal(enumValues.OrderBy(x => x), catalogued.OrderBy(x => x));
    }

    [Fact]
    public void ByCategory_mirrors_All()
    {
        Assert.Equal(ErrorCatalog.All.Count, ErrorCatalog.ByCategory.Count);
        foreach (var info in ErrorCatalog.All)
            Assert.Same(info, ErrorCatalog.ByCategory[info.Category]);
    }

    [Fact]
    public void Info_is_a_strict_lookup()
    {
        // Documenta a intenção: Info() não silencia uma categoria inexistente.
        var undefined = (ErrorCategory)9999;
        Assert.Throws<KeyNotFoundException>(() => ErrorCatalog.Info(undefined));
    }
}
