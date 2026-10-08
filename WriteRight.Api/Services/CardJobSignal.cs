using System.Threading.Channels;

namespace WriteRight.Api.Services;

/// <summary>
/// O "acorda" do <see cref="CardWorker"/>. Não carrega trabalho nenhum — a fila de
/// verdade é o banco (<c>CardsStatus.Pending</c>). Isto só evita que o worker fique
/// consultando o banco em loop à toa.
///
/// Capacidade 1 com descarte: dez correções seguidas viram UM aviso, e basta, porque
/// o worker, ao acordar, processa tudo que estiver pendente. Aviso que chega durante
/// uma passada fica guardado e dispara a próxima — não há aviso perdido.
///
/// Singleton: quem avisa (a requisição de correção) morre antes de o worker acordar.
/// </summary>
public sealed class CardJobSignal
{
    private readonly Channel<byte> _channel =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    public void Notify() => _channel.Writer.TryWrite(0);

    public async Task WaitAsync(CancellationToken ct) => await _channel.Reader.ReadAsync(ct);
}
