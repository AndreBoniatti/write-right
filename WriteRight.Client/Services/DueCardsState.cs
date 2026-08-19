namespace WriteRight.Client.Services;

/// <summary>
/// Quantos cards esperam revisão — estado compartilhado entre quem MUDA esse número
/// (corrigir uma prática, revisar um card, descartar) e quem o EXIBE (o selo do menu).
///
/// Existe porque o menu vive no layout: ele é montado uma vez e sobrevive a toda a
/// navegação do SPA. Sem um canal de aviso, o contador ficaria congelado no valor do
/// boot — e o erro pior não é ficar baixo depois de uma correção, é ficar ALTO depois
/// de você terminar a sessão, mandando fazer trabalho que já acabou.
///
/// <see cref="Set"/> é o caminho normal: quase sempre quem mudou o número já sabe o
/// novo (a revisão devolve <c>RemainingDue</c>, a sessão sabe o tamanho da fila), e
/// então atualizar não custa requisição nenhuma. <see cref="RefreshAsync"/> fica para
/// os casos em que o novo valor não é dedutível — cunhar 3 cards pode somar só 1 à
/// fila, porque irmãos da mesma frase não entram juntos.
/// </summary>
public sealed class DueCardsState
{
    private readonly WriteRightApiClient _api;

    public DueCardsState(WriteRightApiClient api) => _api = api;

    /// <summary>Cards na próxima sessão. Zero até a primeira leitura.</summary>
    public int Count { get; private set; }

    /// <summary>Disparado só quando o número de fato muda.</summary>
    public event Action? Changed;

    /// <summary>Publica um valor já conhecido, sem ida ao servidor.</summary>
    public void Set(int count)
    {
        if (count == Count) return; // re-render à toa é pior que nada
        Count = count;
        Changed?.Invoke();
    }

    /// <summary>
    /// Relê do servidor. Falha em silêncio: o selo é decoração, e derrubar a tela
    /// que acabou de concluir uma correção por causa dele seria trocar um número
    /// errado por um erro de verdade.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try { Set((await _api.GetDueCardsAsync(ct)).Count); }
        catch { /* mantém o último valor conhecido */ }
    }
}
