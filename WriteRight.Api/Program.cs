using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Llm;
using WriteRight.Api.Services;
using WriteRight.Shared.Corrections;
using WriteRight.Shared.Exercises;

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
var api = app.MapGroup("/api");

// Gera um texto pro usuário traduzir.
api.MapPost("/exercises/generate",
    async (ExerciseGenerationRequest request, PracticeService practice, CancellationToken ct) =>
        Results.Ok(await practice.GenerateAsync(request, ct)))
   .WithName("GenerateExercise");

// Corrige a tradução do usuário e persiste a tentativa + os erros.
api.MapPost("/corrections",
    async (CorrectionRequest request, PracticeService practice, CancellationToken ct) =>
        Results.Ok(await practice.CorrectAndSaveAsync(request, ct)))
   .WithName("CorrectTranslation");

// Perfil de fraquezas (agregação dos erros por categoria).
api.MapGet("/profile",
    async (PracticeService practice, CancellationToken ct) =>
        Results.Ok(await practice.GetProfileAsync(ct)))
   .WithName("GetProfile");

app.Run();
