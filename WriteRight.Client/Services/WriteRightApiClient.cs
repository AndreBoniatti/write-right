using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;
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

    public async Task<GeneratedExercise> GenerateAsync(ExerciseGenerationRequest request, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/exercises/generate", request, Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<GeneratedExercise>(Json, ct))!;
    }

    public async Task<CorrectionResult> CorrectAsync(CorrectionRequest request, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/corrections", request, Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CorrectionResult>(Json, ct))!;
    }

    public async Task<ErrorProfile> GetProfileAsync(CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<ErrorProfile>("api/profile", Json, ct))!;
}
