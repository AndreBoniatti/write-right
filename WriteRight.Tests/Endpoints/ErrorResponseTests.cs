using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WriteRight.Api.Llm;
using WriteRight.Shared.Exercises;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Endpoints;

/// <summary>
/// O pipeline de erro de ponta a ponta: a API de verdade em memória, em ambiente de
/// desenvolvimento, com banco SQLite temporário e a IA trocada pelo stub. Testar o
/// filtro isolado não pega o que mais importa aqui — o que acontece com as exceções
/// que ele NÃO trata (o 400 de corpo malformado já virou 500 por isso).
/// </summary>
public sealed class ErrorResponseTests : IDisposable
{
    private const string ClientOrigin = "http://localhost:5193";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"writeright-test-{Guid.NewGuid():N}.db");
    private readonly StubLlmProvider _llm = new(new GeneratedExercise("Um texto gerado."));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public ErrorResponseTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:WriteRight", $"Data Source={_dbPath}");
            web.UseSetting("Llm:ApiKey", " ");
            web.ConfigureTestServices(services => services.AddSingleton<ILlmProvider>(_llm));
        });
        _http = _factory.CreateClient();
        _http.DefaultRequestHeaders.Add("Origin", ClientOrigin);
    }

    private static object Practice(string source = "Portuguese", string target = "English") =>
        new { sourceLanguage = source, targetLanguage = target, wordCount = 60, level = "B1" };

    [Fact]
    public async Task Runs_against_the_temporary_database()
    {
        await _http.GetAsync("/api/practices");

        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task A_refused_request_is_a_400_problem_with_the_reason()
    {
        var response = await _http.PostAsJsonAsync("/api/practices", Practice("English", "English"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Origem e alvo devem ser idiomas diferentes.", problem!.Detail);
    }

    [Fact]
    public async Task A_malformed_body_is_still_a_400()
    {
        var response = await _http.PostAsync("/api/practices", new StringContent("{bad", null, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/profile/errors?category=Bogus")]
    [InlineData("/api/profile/errors")]
    public async Task A_query_that_does_not_bind_is_still_a_400(string url)
    {
        var response = await _http.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_resource_is_a_404_problem()
    {
        var response = await _http.GetAsync("/api/practices/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>A IA respondeu e cobrou, mas a resposta não serviu: o defeito está atrás da API.</summary>
    [Fact]
    public async Task A_failed_llm_call_is_a_502_problem()
    {
        _llm.FailAfterBilling = true;

        var response = await _http.PostAsJsonAsync("/api/practices", Practice());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Sem o cabeçalho, o navegador esconde o erro e o cliente acha que a API caiu.</summary>
    [Fact]
    public async Task Error_responses_keep_the_cors_header()
    {
        var response = await _http.PostAsJsonAsync("/api/practices", Practice("English", "English"));

        Assert.Equal(ClientOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}
