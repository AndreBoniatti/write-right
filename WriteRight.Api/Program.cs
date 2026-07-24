using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Api.Services;
using WriteRight.Shared.Practices;

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
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.AddScoped<ILlmProvider, AnthropicLlmProvider>();
builder.Services.AddScoped<PracticeService>();

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

app.Run();
