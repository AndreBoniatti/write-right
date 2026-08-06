using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteRight.Shared.Analysis;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Client.Services;

/// <summary>Desfecho de um PEDIDO de análise, traduzido do status HTTP.</summary>
public enum GenerateAnalysisStatus
{
    /// <summary>
    /// Enfileirada (202). Não quer dizer pronta — a geração roda em background e o
    /// resultado chega acompanhando <c>GetAnalysisStateAsync</c>.
    /// </summary>
    Accepted,
    /// <summary>Histórico pequeno demais (409).</summary>
    NotEnoughData,
    /// <summary>Falha de rede/servidor.</summary>
    Failed,
}


/// <summary>
/// Cliente tipado da API do WriteRight. Centraliza as chamadas HTTP e as opções
/// de JSON (enums como string, igual à API) num lugar só.
/// </summary>
public sealed class WriteRightApiClient
{
    // Web defaults (camelCase, case-insensitive) + enums como string.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;

    public WriteRightApiClient(HttpClient http) => _http = http;

    /// <summary>Lista as práticas pra tela inicial (mais recente primeiro).</summary>
    public async Task<IReadOnlyList<PracticeSummary>> ListPracticesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<PracticeSummary>>("api/practices", Json, ct) ?? new();

    /// <summary>Detalhe de uma prática (retomar ou ler). Null se não existe (404).</summary>
    public async Task<PracticeDetail?> GetPracticeAsync(int id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/practices/{id}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<PracticeDetail>(Json, ct);
    }

    /// <summary>Cria uma prática (gera o texto + persiste como "Em andamento").</summary>
    public async Task<PracticeDetail> CreatePracticeAsync(CreatePracticeRequest request, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/practices", request, Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PracticeDetail>(Json, ct))!;
    }

    /// <summary>Salva o rascunho da tradução ("Salvar e sair"). True se salvou.</summary>
    public async Task<bool> SaveDraftAsync(int id, string userTranslation, CancellationToken ct = default)
    {
        var resp = await _http.PutAsJsonAsync(
            $"api/practices/{id}/translation", new PracticeTranslationRequest(userTranslation), Json, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Corrige a prática e a conclui. Devolve o detalhe corrigido, ou null se falhou.</summary>
    public async Task<PracticeDetail?> CorrectPracticeAsync(int id, string userTranslation, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"api/practices/{id}/correct", new PracticeTranslationRequest(userTranslation), Json, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<PracticeDetail>(Json, ct)
            : null;
    }

    /// <summary>Exclui uma prática. True se excluiu.</summary>
    public async Task<bool> DeletePracticeAsync(int id, CancellationToken ct = default) =>
        (await _http.DeleteAsync($"api/practices/{id}", ct)).IsSuccessStatusCode;

    /// <summary>Perfil de fraquezas (agregação das práticas concluídas).</summary>
    public async Task<ErrorProfile> GetProfileAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<ErrorProfile>("api/profile", Json, ct))!;

    /// <summary>Os erros reais do usuário numa categoria (tela de revisão do perfil).</summary>
    public async Task<IReadOnlyList<CategoryError>> GetCategoryErrorsAsync(
        ErrorCategory category, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<CategoryError>>(
            $"api/profile/errors?category={category}", Json, ct) ?? new();

    /// <summary>Última análise de fraquezas + se vale gerar outra.</summary>
    public async Task<AnalysisState> GetAnalysisStateAsync(CancellationToken ct = default) =>
        (await _http.GetFromJsonAsync<AnalysisState>("api/analysis", Json, ct))!;

    /// <summary>
    /// PEDE uma análise nova. Volta assim que o servidor aceita (202) — a geração
    /// roda em background, porque a chamada de IA leva minutos e não sobrevive aos
    /// timeouts do caminho (navegador, proxy, balanceador).
    ///
    /// O resultado se acompanha por <see cref="GetAnalysisStateAsync"/>: enquanto
    /// <c>Job.Status</c> for <c>Running</c>, ainda está rodando.
    /// </summary>
    public async Task<GenerateAnalysisStatus> GenerateAnalysisAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsync("api/analysis", content: null, ct);

        if (resp.IsSuccessStatusCode) return GenerateAnalysisStatus.Accepted;

        return resp.StatusCode switch
        {
            HttpStatusCode.Conflict => GenerateAnalysisStatus.NotEnoughData,
            _ => GenerateAnalysisStatus.Failed,
        };
    }
}
