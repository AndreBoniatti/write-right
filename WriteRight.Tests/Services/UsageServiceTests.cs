using WriteRight.Api.Data;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Usage;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Services;

/// <summary>
/// O relatório existe pra responder UMA pergunta com dado real: quanto custa uma
/// prática. Estes testes travam a regra de atribuição por trás dessa média — que é
/// onde mora a chance de o número sair bonito e errado — e o alarme de modelo sem preço.
/// </summary>
public sealed class UsageServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();

    private UsageService Service() => new(_db.NewContext(), TestPricing.Default());

    /// <summary>Grava uma chamada direto no banco — sem IA, sem prática de verdade.</summary>
    private async Task SeedCallAsync(
        LlmOperation operation, decimal? cost, int? practiceId = null, int? analysisId = null,
        string model = "claude-sonnet-5", long input = 1_000, long output = 500)
    {
        await using var ctx = _db.NewContext();
        ctx.LlmCalls.Add(new LlmCall
        {
            Operation = operation,
            Model = model,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = cost,
            CreatedAt = DateTimeOffset.UtcNow,
            PracticeId = practiceId,
            AnalysisId = analysisId,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Uma prática concluída (só pra entrar no denominador da média).</summary>
    private async Task<int> SeedCompletedPracticeAsync()
    {
        await using var ctx = _db.NewContext();
        var practice = new ExerciseAttempt
        {
            SourceLanguage = Language.Portuguese,
            TargetLanguage = Language.English,
            Status = PracticeStatus.Completed,
            SourceText = "seed",
            UserTranslation = "seed",
            CompletedAt = DateTimeOffset.UtcNow,
        };
        ctx.Exercises.Add(practice);
        await ctx.SaveChangesAsync();
        return practice.Id;
    }

    [Fact]
    public async Task GetReportAsync_on_an_empty_database_reports_nothing_instead_of_zero()
    {
        var report = await Service().GetReportAsync();

        Assert.Equal(0, report.TotalCalls);
        Assert.Equal(0m, report.TotalCostUsd);
        Assert.Empty(report.ByOperation);
        // Null, não 0: "nenhum dado" e "custa zero" são conclusões diferentes.
        Assert.Null(report.AvgCostPerPracticeUsd);
        Assert.Null(report.AvgCostPerAnalysisUsd);
        Assert.Null(report.FirstCallAt);
    }

    [Fact]
    public async Task GetReportAsync_groups_by_operation_and_model()
    {
        await SeedCallAsync(LlmOperation.Generation, 0.004m, model: "claude-haiku-4-5");
        await SeedCallAsync(LlmOperation.Correction, 0.026m);
        await SeedCallAsync(LlmOperation.Correction, 0.020m);

        var report = await Service().GetReportAsync();

        Assert.Equal(3, report.TotalCalls);
        Assert.Equal(0.050m, report.TotalCostUsd);

        // Ordenado por custo: a correção é o balde que importa.
        var top = report.ByOperation[0];
        Assert.Equal(LlmOperation.Correction, top.Operation);
        Assert.Equal(2, top.Calls);
        Assert.Equal(0.046m, top.CostUsd);
    }

    [Fact]
    public async Task GetReportAsync_separates_the_same_operation_on_different_models()
    {
        // Trocar o modelo de correção por config não pode somar maçã com laranja.
        await SeedCallAsync(LlmOperation.Correction, 0.026m, model: "claude-sonnet-5");
        await SeedCallAsync(LlmOperation.Correction, 0.050m, model: "claude-opus-5");

        var report = await Service().GetReportAsync();

        Assert.Equal(2, report.ByOperation.Count);
        Assert.All(report.ByOperation, o => Assert.Equal(LlmOperation.Correction, o.Operation));
        Assert.Contains(report.ByOperation, o => o.Model == "claude-opus-5");
    }

    [Fact]
    public async Task AvgCostPerPractice_divides_all_practice_cost_by_completed_practices()
    {
        var practiceId = await SeedCompletedPracticeAsync();
        await SeedCallAsync(LlmOperation.Generation, 0.004m, practiceId: practiceId);
        await SeedCallAsync(LlmOperation.Correction, 0.026m, practiceId: practiceId);
        // Análise NÃO entra no custo de prática — é outra unidade de consumo.
        await SeedCallAsync(LlmOperation.Analysis, 0.050m);

        var report = await Service().GetReportAsync();

        Assert.Equal(1, report.CompletedPractices);
        Assert.Equal(0.030m, report.AvgCostPerPracticeUsd);
        Assert.Equal(0.050m, report.AvgCostPerAnalysisUsd);
    }

    [Fact]
    public async Task AvgCostPerPractice_charges_abandoned_generations_to_the_completed_ones()
    {
        // Uma concluída (geração + correção) e uma abandonada (só geração). O custo da
        // abandonada é real e você pagou por ele — então ele encarece o custo médio de
        // cada prática que chega ao fim. Diluir isso seria maquiar a margem.
        var completed = await SeedCompletedPracticeAsync();
        await SeedCallAsync(LlmOperation.Generation, 0.004m, practiceId: completed);
        await SeedCallAsync(LlmOperation.Correction, 0.026m, practiceId: completed);
        await SeedCallAsync(LlmOperation.Generation, 0.004m, practiceId: 999); // abandonada

        var report = await Service().GetReportAsync();

        Assert.Equal(1, report.CompletedPractices);
        Assert.Equal(0.034m, report.AvgCostPerPracticeUsd);
    }

    [Fact]
    public async Task AvgCostPerPractice_includes_calls_that_never_produced_a_practice()
    {
        // Geração cobrada que falhou antes de criar a prática: fica SEM PracticeId.
        // Por isso a atribuição é por operação — se fosse pelo vínculo, esse gasto
        // sairia da média e o custo por prática apareceria menor do que é.
        var completed = await SeedCompletedPracticeAsync();
        await SeedCallAsync(LlmOperation.Generation, 0.004m, practiceId: completed);
        await SeedCallAsync(LlmOperation.Correction, 0.026m, practiceId: completed);
        await SeedCallAsync(LlmOperation.Generation, 0.004m); // falhou, órfã

        var report = await Service().GetReportAsync();

        Assert.Equal(0.034m, report.AvgCostPerPracticeUsd);
    }

    [Fact]
    public async Task GetReportAsync_flags_calls_whose_model_had_no_price()
    {
        await SeedCallAsync(LlmOperation.Correction, 0.026m);
        await SeedCallAsync(LlmOperation.Correction, cost: null, model: "modelo-novo-sem-preco");

        var report = await Service().GetReportAsync();

        // O total está subestimado, e o relatório DIZ isso em vez de esconder.
        Assert.Equal(2, report.TotalCalls);
        Assert.Equal(1, report.UnpricedCalls);
        Assert.Equal(0.026m, report.TotalCostUsd);
    }

    public void Dispose() => _db.Dispose();
}
