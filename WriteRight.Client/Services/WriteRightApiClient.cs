using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Profile;

namespace WriteRight.Client.Services;

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
}
