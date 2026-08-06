namespace WriteRight.Api.Services;

/// <summary>
/// Consome a <see cref="AnalysisJobQueue"/> e roda a geração fora do ciclo da
/// requisição.
///
/// Dois cuidados que fazem esse padrão funcionar:
///
///  • <b>Escopo próprio.</b> O <c>DbContext</c> e o <c>AnalysisService</c> são
///    scoped, e o escopo da requisição que enfileirou já foi descartado quando o
///    worker acorda. Usar aquele escopo é o bug número um aqui — daí o
///    <see cref="IServiceScopeFactory"/>.
///
///  • <b>O trabalho não é cancelável.</b> O <c>stoppingToken</c> governa só a espera
///    por itens na fila; a geração em si recebe <see cref="CancellationToken.None"/>.
///    Abortar no meio não devolveria o dinheiro (a chamada já está em curso do lado
///    da Anthropic) e só trocaria um gasto contabilizado por um gasto invisível.
/// </summary>
public sealed class AnalysisWorker : BackgroundService
{
    private readonly AnalysisJobQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AnalysisWorker> _log;

    public AnalysisWorker(
        AnalysisJobQueue queue, IServiceScopeFactory scopes, ILogger<AnalysisWorker> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var analysis = scope.ServiceProvider.GetRequiredService<AnalysisService>();

                var (outcome, _) = await analysis.GenerateAsync(CancellationToken.None);

                if (outcome == AnalysisOutcome.Ok) _queue.MarkDone();
                else _queue.MarkFailed(ReasonFor(outcome));
            }
            catch (Exception ex)
            {
                // Nada pode escapar: exceção não tratada aqui mataria o worker e
                // deixaria toda geração futura pendurada em Running.
                _log.LogError(ex, "Falha ao gerar a análise em background.");
                _queue.MarkFailed("Erro inesperado ao gerar a análise. Tente de novo.");
            }
        }
    }

    private static string ReasonFor(AnalysisOutcome outcome) => outcome switch
    {
        AnalysisOutcome.NoGrounding =>
            "A IA respondeu sem evidência válida, então nada foi salvo. Tente de novo.",
        AnalysisOutcome.NotEnoughData =>
            "Histórico insuficiente para uma análise honesta.",
        _ => "Não foi possível gerar a análise.",
    };
}
