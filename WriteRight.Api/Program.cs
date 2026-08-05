using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Api.Services;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Taxonomy;

var builder = WebApplication.CreateBuilder(args);

// ── Serviços ────────────────────────────────────────────────
builder.Services.AddOpenApi();

// Enums viajam como STRING no JSON (bate com o banco e com o structured output).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Banco: SQLite (arquivo local). Connection string do config, com default.
var connectionString = builder.Configuration.GetConnectionString("WriteRight")
    ?? "Data Source=writeright.db";
builder.Services.AddDbContext<WriteRightDbContext>(o => o.UseSqlite(connectionString));

// Provedor de IA (costura) + serviço de orquestração (gera/corrige/perfil).
// As tarifas moram só no appsettings (Llm:Pricing), sem fallback no código — então
// modelo em uso sem preço derruba a app no startup. Ver LlmOptionsValidator.
builder.Services.AddSingleton<IValidateOptions<LlmOptions>, LlmOptionsValidator>();
builder.Services.AddOptions<LlmOptions>()
    .Bind(builder.Configuration.GetSection(LlmOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddScoped<ILlmProvider, AnthropicLlmProvider>();
builder.Services.AddSingleton<LlmPricing>(); // só depende de IOptions — sem estado por request
builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<PracticeService>();
builder.Services.AddScoped<AnalysisService>();

// CORS pro front (Blazor WASM roda em outra origem).
const string clientCors = "WriteRightClient";
builder.Services.AddCors(o => o.AddPolicy(clientCors, p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod())); // dev liberal; restringir origem em prod.

var app = builder.Build();

// Cria/atualiza o banco no startup (dev). Em prod, aplicar migrations no deploy.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<WriteRightDbContext>().Database.Migrate();
}

// ── Pipeline ────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors(clientCors);

// ── Endpoints ───────────────────────────────────────────────
// Práticas: recurso com ciclo de vida (criar → retomar/salvar → corrigir → ler).
var practices = app.MapGroup("/api/practices");

// Cria uma prática: gera o texto e persiste como "Em andamento".
practices.MapPost("/",
    async (CreatePracticeRequest request, PracticeService service, CancellationToken ct) =>
    {
        try
        {
            var detail = await service.CreatePracticeAsync(request, ct);
            return Results.Created($"/api/practices/{detail.Id}", detail);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    })
   .WithName("CreatePractice");

// Lista as práticas pra tela inicial (resumos).
practices.MapGet("/",
    async (PracticeService service, CancellationToken ct) =>
        Results.Ok(await service.ListPracticesAsync(ct)))
   .WithName("ListPractices");

// Detalhe de uma prática (retomar ou ler).
practices.MapGet("/{id:int}",
    async (int id, PracticeService service, CancellationToken ct) =>
        await service.GetPracticeAsync(id, ct) is { } detail
            ? Results.Ok(detail)
            : Results.NotFound())
   .WithName("GetPractice");

// Salva o rascunho da tradução ("Salvar e sair"), sem corrigir.
practices.MapPut("/{id:int}/translation",
    async (int id, PracticeTranslationRequest request, PracticeService service, CancellationToken ct) =>
        (await service.SaveDraftAsync(id, request.UserTranslation, ct)) switch
        {
            PracticeOutcome.Ok => Results.NoContent(),
            PracticeOutcome.ReadOnly => Results.Conflict(),
            _ => Results.NotFound(),
        })
   .WithName("SaveDraft");

// Corrige a prática e a conclui (readonly). Devolve o detalhe já corrigido.
practices.MapPost("/{id:int}/correct",
    async (int id, PracticeTranslationRequest request, PracticeService service, CancellationToken ct) =>
    {
        var (outcome, detail) = await service.CorrectPracticeAsync(id, request.UserTranslation, ct);
        return outcome switch
        {
            PracticeOutcome.Ok => Results.Ok(detail),
            PracticeOutcome.ReadOnly => Results.Conflict(),
            _ => Results.NotFound(),
        };
    })
   .WithName("CorrectPractice");

// Exclui uma prática (com confirmação no cliente).
practices.MapDelete("/{id:int}",
    async (int id, PracticeService service, CancellationToken ct) =>
        (await service.DeletePracticeAsync(id, ct)) == PracticeOutcome.Ok
            ? Results.NoContent()
            : Results.NotFound())
   .WithName("DeletePractice");

// Perfil de fraquezas (agregação dos erros das práticas concluídas).
app.MapGet("/api/profile",
    async (PracticeService service, CancellationToken ct) =>
        Results.Ok(await service.GetProfileAsync(ct)))
   .WithName("GetProfile");

// Erros reais de uma categoria (tela de revisão do perfil). Releitura, sem IA.
app.MapGet("/api/profile/errors",
    async (ErrorCategory category, PracticeService service, CancellationToken ct) =>
        Results.Ok(await service.GetCategoryErrorsAsync(category, ct)))
   .WithName("GetCategoryErrors");

// Análise de fraquezas: a última gerada + se vale gerar outra.
app.MapGet("/api/analysis",
    async (AnalysisService service, CancellationToken ct) =>
        Results.Ok(await service.GetStateAsync(ct)))
   .WithName("GetAnalysisState");

// Gera uma análise nova (chama a IA e persiste).
app.MapPost("/api/analysis",
    async (AnalysisService service, CancellationToken ct) =>
    {
        var (outcome, analysis) = await service.GenerateAsync(ct);
        return outcome switch
        {
            AnalysisOutcome.Ok => Results.Ok(analysis),
            // Histórico pequeno demais: pedido legítimo, estado errado.
            AnalysisOutcome.NotEnoughData => Results.Conflict(),
            // Modelo respondeu sem lastro: nada persistido, dá pra tentar de novo.
            _ => Results.UnprocessableEntity(),
        };
    })
   .WithName("GenerateAnalysis");

// Consumo da IA: quanto custou, por operação, e a média por prática/análise.
// Releitura pura do registrado — não chama a IA.
app.MapGet("/api/usage",
    async (UsageService service, CancellationToken ct) =>
        Results.Ok(await service.GetReportAsync(ct)))
   .WithName("GetUsageReport");

app.Run();
