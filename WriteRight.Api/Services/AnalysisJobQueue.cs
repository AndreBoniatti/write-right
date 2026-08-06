using System.Threading.Channels;
using WriteRight.Shared.Analysis;

namespace WriteRight.Api.Services;

/// <summary>
/// Fila de uma posição para a geração de análise, com o estado da execução corrente.
///
/// <b>Em memória, de propósito (por ora).</b> Se o processo reiniciar no meio, o job
/// se perde e o usuário só precisa pedir de novo — nada é corrompido, porque o que
/// já foi pago é gravado por <see cref="UsageService.AfterBilling"/> antes de
/// qualquer coisa. Durabilidade de verdade pede uma linha de job no banco; é o
/// próximo passo se isto virar produto, não agora.
///
/// Uma posição só porque não faz sentido enfileirar duas análises do mesmo histórico:
/// a segunda leria quase o mesmo material e cobraria de novo. Pedido durante uma
/// execução é absorvido em silêncio — o cliente está fazendo polling e vai ver
/// <see cref="AnalysisJobStatus.Running"/> de qualquer forma.
///
/// Singleton: o estado precisa sobreviver ao fim da requisição que enfileirou.
/// </summary>
public sealed class AnalysisJobQueue
{
    private readonly Channel<byte> _channel =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly Lock _gate = new();
    private AnalysisJob _current = AnalysisJob.Idle;

    /// <summary>Situação atual, para o <c>GET /api/analysis</c>.</summary>
    public AnalysisJob Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// Enfileira uma geração. Devolve <c>false</c> se já existe uma em curso — e aí
    /// não há nada a fazer: quem pediu vai ver <c>Running</c> no próximo polling.
    ///
    /// O estado vira <c>Running</c> aqui, e não quando o worker pega o item, para não
    /// existir uma janela em que o usuário pediu e a tela ainda diz <c>Idle</c>.
    /// </summary>
    public bool TryEnqueue()
    {
        lock (_gate)
        {
            if (_current.Status == AnalysisJobStatus.Running) return false;
            _current = AnalysisJob.Started(DateTimeOffset.UtcNow);
        }

        if (_channel.Writer.TryWrite(0)) return true;

        // Não deveria acontecer (capacidade 1 e não havia execução em curso), mas
        // deixar Running sem ninguém para processar prenderia a tela para sempre.
        lock (_gate) _current = AnalysisJob.Idle;
        return false;
    }

    public IAsyncEnumerable<byte> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void MarkDone()
    {
        lock (_gate) _current = AnalysisJob.Idle;
    }

    public void MarkFailed(string reason)
    {
        lock (_gate) _current = AnalysisJob.Failure(reason);
    }
}
