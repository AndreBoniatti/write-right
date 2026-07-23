using System.Text.Json;
using WriteRight.Api.Llm;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Llm;

/// <summary>
/// O structured output só é confiável se o JSON schema enviado ao Claude ficar em
/// sincronia com a taxonomia. O schema deriva os valores via <c>Enum.GetNames</c>;
/// estes testes travam a FORMA do schema e a cobertura total dos enums (o caminho
/// de propriedades aninhadas é fácil de quebrar num refactor e não é tautológico).
/// </summary>
public class CorrectionSchemaTests
{
    private static readonly JsonElement Schema =
        JsonSerializer.SerializeToElement(CorrectionPrompt.BuildResultSchema());

    [Fact]
    public void Top_level_requires_the_result_fields()
    {
        Assert.Equal("object", Schema.GetProperty("type").GetString());
        Assert.False(Schema.GetProperty("additionalProperties").GetBoolean());

        var required = Schema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        Assert.Equal(new[] { "correctedText", "errors", "overallComment" }, required);
    }

    [Fact]
    public void Error_item_requires_all_five_fields()
    {
        var required = ErrorItem().GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()!).ToList();

        Assert.Equal(
            new[] { "category", "severity", "original", "correction", "explanation" },
            required);
    }

    [Fact]
    public void Category_enum_lists_every_ErrorCategory()
    {
        Assert.Equal(
            Enum.GetNames<ErrorCategory>().OrderBy(x => x),
            EnumValuesAt("category").OrderBy(x => x));
    }

    [Fact]
    public void Severity_enum_lists_every_ErrorSeverity()
    {
        Assert.Equal(
            Enum.GetNames<ErrorSeverity>().OrderBy(x => x),
            EnumValuesAt("severity").OrderBy(x => x));
    }

    private static JsonElement ErrorItem() =>
        Schema.GetProperty("properties").GetProperty("errors").GetProperty("items");

    private static List<string> EnumValuesAt(string property) =>
        ErrorItem().GetProperty("properties").GetProperty(property).GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
}
