using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Usage;

namespace WriteRight.Api.Services;

/// <summary>
/// Registra e agrega o consumo da IA.
///
/// Duas responsabilidades, de propósito na mesma classe: quem grava e quem lê o
/// custo compartilham a mesma regra de atribuição (o que conta como "custo de uma
/// prática"), e separá-las só faria essa regra existir em dois lugares.
///
/// O registro é <b>não-transacional em relação ao trabalho</b>: chama-se
/// <see cref="Record"/> depois de a chamada à IA ter voltado, e o gasto é gravado
/// mesmo quando o resultado é descartado (análise sem lastro). Dinheiro gasto é
/// fato consumado; não pode depender do desfecho lógico.
/// </summary>
public sealed class UsageService
{
    /// <summary>
    /// Token deliberadamente NÃO cancelável, para tudo que grava DEPOIS da chamada
    /// à IA.
    ///
    /// O <c>ct</c> de uma requisição minimal API é o <c>HttpContext.RequestAborted</c>:
    /// ele é cancelado no instante em que o navegador desiste — e o cliente Blazor
    /// desiste sozinho no timeout padrão de 100s do <c>HttpClient</c>. Usar esse token
    /// depois da chamada descartaria exatamente o registro do dinheiro já gasto e o
    /// resultado já pago, que é o pior momento possível para desistir.
    ///
    /// Antes da chamada o <c>ct</c> normal continua valendo: cancelar uma leitura que
    /// ainda não custou nada é o comportamento certo.
    /// </summary>
    public static CancellationToken AfterBilling => CancellationToken.None;

    private readonly WriteRightDbContext _db;
    private readonly LlmPricing _pricing;

    public UsageService(WriteRightDbContext db, LlmPricing pricing)
    {
        _db = db;
        _pricing = pricing;
    }

    /// <summary>
    /// Enfileira o registro de uma chamada no contexto. NÃO salva — quem chama
    /// decide o momento do <c>SaveChanges</c>, pra o registro entrar na mesma
    /// transação do trabalho que ele acompanha.
    /// </summary>
    public void Record(
        LlmOperation operation, LlmUsage usage, int? practiceId = null, int? analysisId = null)
    {
        _db.LlmCalls.Add(new LlmCall
        {
            Operation = operation,
            Model = usage.Model,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CostUsd = _pricing.CostOf(usage),
            CreatedAt = DateTimeOffset.UtcNow,
            PracticeId = practiceId,
            AnalysisId = analysisId,
        });
    }

    /// <summary>
    /// Relatório de consumo. Agrega em memória pelo mesmo motivo do resto do app: o
    /// SQLite não soma <c>decimal</c> (guardado como TEXT) nem ordena
    /// <c>DateTimeOffset</c>. Volume pessoal — custo irrelevante.
    /// </summary>
    public async Task<UsageReport> GetReportAsync(CancellationToken ct = default)
    {
        var calls = await _db.LlmCalls.ToListAsync(ct);

        var completedPractices = await _db.Exercises
            .CountAsync(p => p.Status == PracticeStatus.Completed, ct);

        var byOperation = calls
            .GroupBy(c => (c.Operation, c.Model))
            .Select(g => new UsageByOperation(
                g.Key.Operation,
                g.Key.Model,
                g.Count(),
                g.Sum(c => c.InputTokens),
                g.Sum(c => c.OutputTokens),
                g.Sum(c => c.CacheWriteTokens),
                g.Sum(c => c.CacheReadTokens),
                g.Sum(c => c.CostUsd ?? 0m)))
            .OrderByDescending(o => o.CostUsd)
            .ThenBy(o => o.Operation)
            .ToList();

        // Custo de prática = toda geração e correção, inclusive a de práticas
        // abandonadas e a de chamadas que falharam sem gerar prática nenhuma (essas
        // ficam sem PracticeId, por isso o corte é por OPERAÇÃO e não pelo vínculo).
        // Dividir pelas CONCLUÍDAS é o número honesto: abandono e falha são custo real.
        var practiceCost = calls
            .Where(c => c.Operation != LlmOperation.Analysis)
            .Sum(c => c.CostUsd ?? 0m);

        var analysisCalls = calls.Where(c => c.Operation == LlmOperation.Analysis).ToList();
        var analysisCost = analysisCalls.Sum(c => c.CostUsd ?? 0m);

        return new UsageReport(
            TotalCalls: calls.Count,
            TotalCostUsd: calls.Sum(c => c.CostUsd ?? 0m),
            UnpricedCalls: calls.Count(c => c.CostUsd is null),
            ByOperation: byOperation,
            CompletedPractices: completedPractices,
            AvgCostPerPracticeUsd: completedPractices > 0 ? practiceCost / completedPractices : null,
            AnalysisCalls: analysisCalls.Count,
            AvgCostPerAnalysisUsd: analysisCalls.Count > 0 ? analysisCost / analysisCalls.Count : null,
            FirstCallAt: calls.Count == 0 ? null : calls.Min(c => c.CreatedAt),
            LastCallAt: calls.Count == 0 ? null : calls.Max(c => c.CreatedAt));
    }
}
