using System.Text.Json;
using WriteRight.Api.Llm;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Llm;

/// <summary>
/// O contrato de fio com a IA: o JSON que o Claude devolve (categorias e
/// severidades como STRING) precisa desserializar nos enums certos, usando as
/// MESMAS opções que o provider usa em produção (<see cref="LlmJson.Options"/>).
/// É o vínculo enum-as-string que sustenta banco, API e cliente — se ele quebra,
/// o perfil de fraquezas corrompe silenciosamente.
/// </summary>
public class LlmJsonTests
{
    [Fact]
    public void Deserializes_a_correction_payload_with_enum_strings()
    {
        // Formato exato do structured output do Claude.
        const string json = """
        {
          "correctedText": "It was a simple day.",
          "errors": [
            {
              "category": "SubjectOmission",
              "severity": "BreaksMeaning",
              "original": "Was a simple day",
              "correction": "It was a simple day",
              "explanation": "O inglês exige um sujeito."
            },
            {
              "category": "Preposition",
              "severity": "Understandable",
              "original": "excited with",
              "correction": "excited about",
              "explanation": "Preposição errada."
            }
          ],
          "overallComment": "Bom trabalho!"
        }
        """;

        var result = JsonSerializer.Deserialize<CorrectionResult>(json, LlmJson.Options);

        Assert.NotNull(result);
        Assert.Equal("It was a simple day.", result!.CorrectedText);
        Assert.Equal("Bom trabalho!", result.OverallComment);
        Assert.Equal(2, result.Errors.Count);

        Assert.Equal(ErrorCategory.SubjectOmission, result.Errors[0].Category);
        Assert.Equal(ErrorSeverity.BreaksMeaning, result.Errors[0].Severity);
        Assert.Equal(ErrorCategory.Preposition, result.Errors[1].Category);
        Assert.Equal(ErrorSeverity.Understandable, result.Errors[1].Severity);
    }

    [Fact]
    public void Property_names_are_case_insensitive()
    {
        // O provider habilita PropertyNameCaseInsensitive: PascalCase também entra
        // (robustez contra variação de casing na resposta do modelo).
        const string json = """{ "CorrectedText": "ok", "Errors": [], "OverallComment": "c" }""";

        var result = JsonSerializer.Deserialize<CorrectionResult>(json, LlmJson.Options);

        Assert.NotNull(result);
        Assert.Equal("ok", result!.CorrectedText);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [MemberData(nameof(EveryCategory))]
    public void Every_category_name_round_trips_as_string(ErrorCategory category)
    {
        // enum → string → enum, com as opções reais. Se um nome de categoria deixar
        // de bater como string, o histórico gravado no banco (string) para de agregar.
        var json = JsonSerializer.Serialize(category, LlmJson.Options);
        Assert.Equal($"\"{category}\"", json);

        var back = JsonSerializer.Deserialize<ErrorCategory>(json, LlmJson.Options);
        Assert.Equal(category, back);
    }

    public static IEnumerable<object[]> EveryCategory() =>
        Enum.GetValues<ErrorCategory>().Select(c => new object[] { c });
}
