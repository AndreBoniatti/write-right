using System.Text.Json;
using WriteRight.Api.Llm;
using WriteRight.Shared.Analysis;
using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Tests.Llm;

/// <summary>
/// O prompt e o schema da análise carregam as travas do desenho: evidência por id,
/// teto de padrões e permissão explícita pra devolver poucos. Se algo disso sumir do
/// texto, a feature degrada pra conselho genérico sem quebrar nada — daí estes testes.
/// </summary>
public class AnalysisPromptTests
{
    private static AnalysisRequest Request(int errors = 3) => new(
        Enumerable.Range(1, errors)
            .Select(i => new AnalysisErrorRow(
                i * 10, ErrorCategory.Preposition, ErrorSeverity.Understandable,
                $"in Monday{i}", $"on Monday{i}", $"'on' com dias da semana ({i})"))
            .ToList(),
        new[] { new CategoryWeight(ErrorCategory.Preposition, 7, 14, new SeverityCounts(0, 7, 0)) },
        PracticesAnalyzed: 6,
        LifetimePractices: 11,
        MaxPatterns: 5,
        MinEvidence: 3);

    // ── System prompt ────────────────────────────────────────────────────────

    [Fact]
    public void System_prompt_states_the_evidence_rule_with_the_real_minimum()
    {
        var prompt = AnalysisPrompt.BuildSystemPrompt(Request());

        Assert.Contains("pelo menos 3 ids", prompt);
        Assert.Contains("No máximo 5 padrões", prompt);
    }

    [Fact]
    public void System_prompt_allows_answering_with_fewer_patterns()
    {
        // O teto protege da lista de dez; o PISO baixo é o que impede o preenchimento
        // forçado — sem essa licença explícita o modelo estica padrão pra fechar a conta.
        var prompt = AnalysisPrompt.BuildSystemPrompt(Request());

        Assert.Contains("Não preencha o teto", prompt);
        Assert.Contains("devolva 1", prompt);
    }

    [Fact]
    public void System_prompt_forbids_the_known_failure_modes()
    {
        var prompt = AnalysisPrompt.BuildSystemPrompt(Request());

        Assert.Contains("CEFR", prompt);                 // nada de estimar nível
        Assert.Contains("links", prompt);                // nada de recurso inventado
        Assert.Contains("tamanho de frase", prompt);     // o ponto cego assumido
        Assert.Contains("motivacional", prompt);
    }

    [Fact]
    public void System_prompt_keeps_each_field_to_its_own_job()
    {
        // A tela mostra, em sequência, a categoria, os erros citados, o diagnóstico e a
        // regra. Diagnóstico que relista os erros e dá o conselho repete o que está logo
        // ao lado; título que abre com a categoria repete o selo.
        var prompt = AnalysisPrompt.BuildSystemPrompt(Request());

        Assert.Contains("NÃO comece pelo nome da categoria", prompt);
        Assert.Contains("1 ou 2 frases com o PORQUÊ", prompt);
        Assert.Contains("não dê o conselho (ele vai em 'study')", prompt);
    }

    [Fact]
    public void System_prompt_carries_the_category_vocabulary()
    {
        var prompt = AnalysisPrompt.BuildSystemPrompt(Request());

        Assert.All(
            Enum.GetNames<ErrorCategory>(),
            name => Assert.Contains(name, prompt));
    }

    // ── User message ─────────────────────────────────────────────────────────

    [Fact]
    public void User_message_lists_each_error_with_the_id_the_evidence_will_cite()
    {
        var request = Request();
        var message = AnalysisPrompt.BuildUserMessage(request);

        foreach (var e in request.Errors)
        {
            Assert.Contains(e.Id.ToString(), message);
            Assert.Contains(e.Original, message);
            Assert.Contains(e.Correction, message);
            Assert.Contains(e.Explanation, message); // o porquê vai INTEIRO — é o contexto
        }
    }

    [Fact]
    public void User_message_includes_the_lifetime_aggregate_and_the_window_size()
    {
        var message = AnalysisPrompt.BuildUserMessage(Request());

        Assert.Contains("Preposition | 7 | 14", message);
        Assert.Contains("as 6 práticas", message);
        Assert.Contains("11", message); // total de práticas no histórico
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    [Fact]
    public void Schema_requires_only_patterns()
    {
        // Sem lista de estudo à parte: ela repetia os padrões, e parear pela posição
        // quebrava quando a conferência descartava um padrão do meio.
        var schema = Schema();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "patterns" },
            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(schema.GetProperty("properties").TryGetProperty("studyItems", out _));
    }

    /// <summary>
    /// O structured output da Anthropic rejeita restrição de cardinalidade em array
    /// (400: "property 'maxItems' is not supported"). Este teste trava a ausência —
    /// se alguém reintroduzir os limites "pra ficar mais seguro", toda geração passa
    /// a estourar em produção, e nenhum outro teste pegaria (o stub não valida schema).
    /// O teto e o piso são garantidos pelo servidor; ver <c>AnalysisServiceTests</c>.
    /// </summary>
    [Fact]
    public void Schema_avoids_array_cardinality_keywords_that_the_api_rejects()
    {
        var patterns = Schema().GetProperty("properties").GetProperty("patterns");
        var evidence = patterns.GetProperty("items").GetProperty("properties")
            .GetProperty("evidenceErrorIds");

        foreach (var array in new[] { patterns, evidence })
        {
            Assert.False(array.TryGetProperty("minItems", out _));
            Assert.False(array.TryGetProperty("maxItems", out _));
        }

        Assert.Equal("integer", evidence.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void Pattern_item_never_asks_the_model_for_categories()
    {
        // Categoria é DERIVADA da evidência no servidor. Pedir ao modelo seria abrir
        // mais uma superfície pra ele errar sem ninguém conferir.
        var item = Schema().GetProperty("properties").GetProperty("patterns")
            .GetProperty("items");

        Assert.Equal(
            new[] { "title", "diagnosis", "evidenceErrorIds", "study" },
            item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(item.GetProperty("properties").TryGetProperty("categories", out _));
    }

    [Fact]
    public void Study_lives_inside_the_pattern_and_lists_every_StudyItemKind()
    {
        var study = Schema().GetProperty("properties").GetProperty("patterns")
            .GetProperty("items").GetProperty("properties").GetProperty("study");
        var kind = study.GetProperty("properties").GetProperty("kind");

        Assert.Equal(
            new[] { "kind", "title", "content" },
            study.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(study.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            Enum.GetNames<StudyItemKind>().OrderBy(x => x),
            kind.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).OrderBy(x => x));
    }

    private static JsonElement Schema() =>
        JsonSerializer.SerializeToElement(AnalysisPrompt.BuildResultSchema());
}
