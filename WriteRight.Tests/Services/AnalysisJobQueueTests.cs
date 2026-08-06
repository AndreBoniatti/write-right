using WriteRight.Api.Services;
using WriteRight.Shared.Analysis;

namespace WriteRight.Tests.Services;

/// <summary>
/// A fila é o que impede a tela de ficar presa: se o estado sair de <c>Running</c> e
/// não voltar, o usuário fica olhando "Analisando…" pra sempre; se entrar em
/// <c>Running</c> e ninguém processar, idem. Estes testes travam as transições.
/// </summary>
public class AnalysisJobQueueTests
{
    [Fact]
    public void Starts_idle()
    {
        Assert.Equal(AnalysisJobStatus.Idle, new AnalysisJobQueue().Current.Status);
    }

    [Fact]
    public void Enqueue_marks_running_immediately()
    {
        // Running é assumido no ENFILEIRAMENTO, não quando o worker acorda: senão
        // existiria uma janela em que o usuário pediu e a tela ainda diz Idle.
        var queue = new AnalysisJobQueue();

        Assert.True(queue.TryEnqueue());
        Assert.Equal(AnalysisJobStatus.Running, queue.Current.Status);
        Assert.NotNull(queue.Current.StartedAt);
    }

    [Fact]
    public void Second_enqueue_while_running_is_refused()
    {
        // Não faz sentido enfileirar duas análises do mesmo histórico: a segunda leria
        // quase o mesmo material e cobraria de novo.
        var queue = new AnalysisJobQueue();
        queue.TryEnqueue();

        Assert.False(queue.TryEnqueue());
        Assert.Equal(AnalysisJobStatus.Running, queue.Current.Status);
    }

    [Fact]
    public void Enqueue_is_allowed_again_after_completion()
    {
        var queue = new AnalysisJobQueue();
        queue.TryEnqueue();
        queue.MarkDone();

        Assert.Equal(AnalysisJobStatus.Idle, queue.Current.Status);
        Assert.True(queue.TryEnqueue());
    }

    [Fact]
    public void Failure_is_reported_with_a_reason_and_does_not_block_retrying()
    {
        // Falha tem que virar mensagem na tela E liberar nova tentativa — travar
        // depois de um erro seria o pior dos dois mundos.
        var queue = new AnalysisJobQueue();
        queue.TryEnqueue();
        queue.MarkFailed("sem evidência válida");

        Assert.Equal(AnalysisJobStatus.Failed, queue.Current.Status);
        Assert.Equal("sem evidência válida", queue.Current.FailureReason);
        Assert.True(queue.TryEnqueue());
    }

    [Fact]
    public async Task Enqueued_work_is_readable_by_the_worker()
    {
        var queue = new AnalysisJobQueue();
        queue.TryEnqueue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = 0;
        await foreach (var _ in queue.ReadAllAsync(cts.Token))
        {
            received++;
            break; // um item basta
        }

        Assert.Equal(1, received);
    }
}
