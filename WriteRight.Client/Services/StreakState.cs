using WriteRight.Shared.Practices;

namespace WriteRight.Client.Services;

/// <summary>
/// Os instantes de conclusão das práticas, de onde sai a série de dias da barra de
/// cima. Existe pelo mesmo motivo do <see cref="DueCardsState"/>: a barra vive no
/// layout, montada uma vez, e quem conclui uma prática está em outra tela. Sem aviso,
/// a chama continuaria apagada depois de você praticar. Excluir não avisa: prática
/// excluída continua contando o dia em que foi feita.
///
/// Guarda os instantes, não a série pronta: a série depende do agora — o dia vira
/// às 4h, inclusive com o app aberto — então é recalculada a cada leitura de
/// <see cref="View"/>. Com algumas centenas de datas, recalcular custa nada.
/// </summary>
public sealed class StreakState
{
    private readonly WriteRightApiClient _api;
    private IReadOnlyList<DateTimeOffset>? _completions;

    public StreakState(WriteRightApiClient api) => _api = api;

    /// <summary>A série neste instante, no fuso do navegador. Null até a primeira leitura.</summary>
    public StreakView? View => _completions is null
        ? null
        : PracticeStreak.Compute(_completions, DateTimeOffset.Now, TimeZoneInfo.Local);

    public event Action? Changed;

    /// <summary>
    /// Relê do servidor. Falha em silêncio, pelo mesmo motivo do selo de cards: a
    /// chama é decoração, e derrubar a tela que acabou de corrigir uma prática por
    /// causa dela seria trocar um número velho por um erro de verdade.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            _completions = await _api.ListCompletionTimesAsync(ct);
            Changed?.Invoke();
        }
        catch { /* mantém o último valor conhecido */ }
    }
}
