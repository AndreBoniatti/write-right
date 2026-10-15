using WriteRight.Shared.Cards;

namespace WriteRight.Client.Services;

/// <summary>
/// Quantos cards esperam revisão — estado compartilhado entre quem MUDA esse número
/// (revisar um card, descartar, corrigir uma prática) e quem o EXIBE (o selo do menu).
///
/// Existe porque o menu vive no layout: ele é montado uma vez e sobrevive a toda a
/// navegação do SPA. Sem um canal de aviso, o contador ficaria congelado no valor do
/// boot — e o erro pior não é ficar baixo depois de uma correção, é ficar ALTO depois
/// de você terminar a sessão, mandando fazer trabalho que já acabou.
///
/// <see cref="Set(int)"/> é o caminho normal: quase sempre quem mudou o número já
/// sabe o novo (a revisão devolve <c>RemainingDue</c>, a sessão sabe o tamanho da
/// fila), e então atualizar não custa requisição nenhuma. A exceção é a correção: os
/// cards nascem segundos depois, fora da requisição, e ninguém avisa quando — daí
/// <see cref="Follow"/>.
/// </summary>
public sealed class DueCardsState
{
    /// <summary>De quanto em quanto tempo se pergunta se os cards ficaram prontos.</summary>
    private static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Teto do acompanhamento. Cobre com folga o caso normal, inclusive as esperas
    /// entre as tentativas do worker (10s e 40s) — mas não tudo: a chamada ao modelo
    /// não tem timeout próprio, e as práticas pendentes saem uma de cada vez. Passou
    /// disso, perguntar a cada 3s não acelera nada; a próxima leitura que achar
    /// prática pendente (o deck, o boot) recomeça.
    /// </summary>
    private static readonly TimeSpan FollowTimeout = TimeSpan.FromMinutes(5);

    private readonly WriteRightApiClient _api;
    private bool _following;

    /// <summary>
    /// Sobe a cada mudança feita aqui no cliente. O acompanhamento compara antes e
    /// depois de cada leitura: se mudou no meio, a resposta pode ter saído antes da
    /// mudança, e publicá-la a desfaria.
    /// </summary>
    private int _version;

    public DueCardsState(WriteRightApiClient api) => _api = api;

    /// <summary>Cards na próxima sessão. Zero até a primeira leitura.</summary>
    public int Count { get; private set; }

    /// <summary>Práticas corrigidas cujos cards ainda estão sendo feitos.</summary>
    public int PendingPractices { get; private set; }

    /// <summary>
    /// Disparado só quando um dos dois números de fato muda. O pendente conta à parte
    /// porque uma prática pode terminar sem mexer no selo — nenhum erro virou card, ou
    /// a criação falhou —, e o deck ainda precisa saber que ela saiu da fila.
    /// </summary>
    public event Action? Changed;

    /// <summary>Publica um valor já conhecido, sem ida ao servidor.</summary>
    public void Set(int count)
    {
        _version++;
        Publish(count, PendingPractices);
    }

    /// <summary>
    /// Publica o resumo que acabou de chegar do servidor. Se ainda há cards sendo
    /// feitos, passa a acompanhá-los.
    /// </summary>
    public void Set(DeckSummary summary)
    {
        _version++;
        Publish(summary.Due, summary.PendingPractices);
        if (summary.PendingPractices > 0) Follow();
    }

    /// <summary>
    /// Relê do servidor. Falha em silêncio: o selo é decoração, e derrubar a tela
    /// que acabou de concluir uma ação por causa dele seria trocar um número
    /// errado por um erro de verdade.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try { Set(await _api.GetDeckSummaryAsync(ct)); }
        catch { /* mantém o último valor conhecido */ }
    }

    /// <summary>
    /// Há cards a caminho: relê o resumo a cada <see cref="FollowInterval"/> até não
    /// sobrar prática pendente. Não espera — volta na hora, e o acompanhamento segue
    /// por baixo, sobrevivendo à navegação. Chamar de novo durante um acompanhamento
    /// não abre outro: o que já está rodando descarta a leitura em curso (ela pode ter
    /// saído antes de a correção nova ser gravada) e pega a prática nova na próxima.
    /// </summary>
    public void Follow()
    {
        _version++;
        if (_following) return;
        _following = true;
        _ = FollowAsync();
    }

    private async Task FollowAsync()
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow + FollowTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(FollowInterval);

                var version = _version;
                DeckSummary summary;
                try { summary = await _api.GetDeckSummaryAsync(); }
                catch { continue; } // API reiniciando, rede piscando: tenta na próxima volta

                // Alguém mexeu no número enquanto a leitura ia e voltava (uma revisão,
                // uma correção nova). Publicá-la poderia desfazer isso — e o pior caso é
                // justamente o selo voltar a subir depois de você terminar a sessão.
                if (version != _version) continue;

                Set(summary); // o Follow() de dentro é no-op
                if (summary.PendingPractices == 0) return;
            }
        }
        finally
        {
            _following = false;
        }
    }

    private void Publish(int count, int pending)
    {
        if (count == Count && pending == PendingPractices) return; // re-render à toa é pior que nada
        Count = count;
        PendingPractices = pending;
        Changed?.Invoke();
    }
}
