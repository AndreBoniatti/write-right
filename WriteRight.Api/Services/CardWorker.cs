using WriteRight.Api.Data;

namespace WriteRight.Api.Services;

/// <summary>
/// Cunha os cards das práticas corrigidas, fora do ciclo da requisição.
///
/// No boot, processa o que ficou pendente (o app caiu no meio, ou foi desligado
/// antes de terminar); depois dorme até o <see cref="CardJobSignal"/> avisar de uma
/// correção nova. Os mesmos dois cuidados do <see cref="AnalysisWorker"/>: escopo
/// próprio por prática e trabalho não cancelável.
///
/// Cada prática tem <see cref="MaxAttempts"/> tentativas, com espera crescente — o
/// caso típico é a API sobrecarregada, que passa sozinha. Esgotadas, a prática vira
/// <see cref="CardsStatus.Failed"/> e sai da fila: tentar pra sempre seria pagar pra
/// sempre por um erro que se repete.
/// </summary>
public sealed class CardWorker : BackgroundService
{
    private const int MaxAttempts = 3;

    private readonly CardJobSignal _signal;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CardWorker> _log;

    public CardWorker(CardJobSignal signal, IServiceScopeFactory scopes, ILogger<CardWorker> log)
    {
        _signal = signal;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nada pode escapar: exceção não tratada aqui mataria o worker, e toda
                // correção futura ficaria sem card, em silêncio. O que chega aqui é falha
                // FORA de uma prática (o banco, tipicamente) — nenhuma chamada paga —,
                // então tenta a passada de novo em breve, em vez de deixar a fila parada
                // até a próxima correção.
                _log.LogError(ex, "Falha inesperada ao processar a fila de cards.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            await _signal.WaitAsync(stoppingToken);
        }
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<int> pending;
        using (var scope = _scopes.CreateScope())
            pending = await scope.ServiceProvider.GetRequiredService<CardMintingService>().PendingAsync(stoppingToken);

        foreach (var practiceId in pending)
            await MintWithRetriesAsync(practiceId, stoppingToken);
    }

    private async Task MintWithRetriesAsync(int practiceId, CancellationToken stoppingToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var minted = await scope.ServiceProvider.GetRequiredService<CardMintingService>().MintAsync(practiceId);
                _log.LogInformation("Prática {PracticeId}: {Minted} card(s) novo(s).", practiceId, minted);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                _log.LogWarning(ex, "Prática {PracticeId}: cards falharam (tentativa {Attempt} de {Max}).",
                    practiceId, attempt, MaxAttempts);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Prática {PracticeId}: cards desistidos depois de {Max} tentativas.",
                    practiceId, MaxAttempts);

                // Protegido à parte: se marcar a falha também falhar, a prática fica
                // pendente (a próxima passada tenta de novo) — mas a passada atual segue
                // pras outras, em vez de abandonar a fila inteira por causa de uma.
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<CardMintingService>().MarkFailedAsync(practiceId);
                }
                catch (Exception markEx)
                {
                    _log.LogError(markEx, "Prática {PracticeId}: não deu pra marcar a falha; segue pendente.", practiceId);
                }
                return;
            }

            // 10s, depois 40s. A espera respeita o desligamento: parar o app no meio
            // dela deixa a prática pendente, e o próximo boot retoma.
            await Task.Delay(TimeSpan.FromSeconds(10 * Math.Pow(4, attempt - 1)), stoppingToken);
        }
    }
}
