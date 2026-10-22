using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using WriteRight.Api.Data;
using WriteRight.Api.Endpoints;
using WriteRight.Api.Llm;
using WriteRight.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Serviços ────────────────────────────────────────────────
// Enums viajam como STRING no JSON (bate com o banco e com o structured output).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Erros como ProblemDetails: os que o app lança de propósito pelo ApiExceptionFilter,
// e os 4xx sem corpo (404, 409) pelo UseStatusCodePages.
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("WriteRight")
    ?? "Data Source=writeright.db";
builder.Services.AddDbContext<WriteRightDbContext>(o => o.UseSqlite(connectionString));

// As tarifas moram só no appsettings (Llm:Pricing), sem fallback no código — então
// modelo em uso sem preço derruba a app no startup. Ver LlmOptionsValidator.
builder.Services.AddSingleton<IValidateOptions<LlmOptions>, LlmOptionsValidator>();
builder.Services.AddOptions<LlmOptions>()
    .Bind(builder.Configuration.GetSection(LlmOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<ILlmProvider, AnthropicLlmProvider>();
builder.Services.AddSingleton<LlmPricing>();
builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<PracticeService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddScoped<CardService>();
builder.Services.AddScoped<CardMintingService>();

// A análise roda fora da requisição: fila em memória (singleton, sobrevive ao fim do
// request) + worker que a consome. Ver AnalysisJobQueue.
builder.Services.AddSingleton<AnalysisJobQueue>();
builder.Services.AddHostedService<AnalysisWorker>();

// Os cards também — mas com a fila no BANCO (CardsStatus), não em memória: perder
// um job de análise se resolve pedindo de novo; perder os cards de uma prática não
// tem "de novo". O sinal só acorda o worker. Ver CardsStatus.
builder.Services.AddSingleton<CardJobSignal>();
builder.Services.AddHostedService<CardWorker>();

// CORS só pra origem do cliente, nunca "qualquer uma": com AllowAnyOrigin, qualquer
// site aberto no navegador enquanto a API roda podia criar e corrigir práticas —
// gastando o crédito da Anthropic.
const string clientCors = "WriteRightClient";
var clientOrigins = builder.Configuration.GetSection("ClientOrigins").Get<string[]>()
    ?? ["http://localhost:5193"];
builder.Services.AddCors(o => o.AddPolicy(clientCors, p =>
    p.WithOrigins(clientOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Cria/atualiza o banco no startup (dev). Em prod, aplicar migrations no deploy.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<WriteRightDbContext>().Database.Migrate();
}

// ── Pipeline ────────────────────────────────────────────────
app.UseStatusCodePages();

// Sem redirecionamento pra HTTPS, de propósito: API e cliente rodam os dois em HTTP
// (ver README), e um 307 pra https quebraria o preflight de CORS do cliente.
app.UseCors(clientCors);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// ── Endpoints ───────────────────────────────────────────────
var api = app.MapGroup("").AddEndpointFilter<ApiExceptionFilter>();
api.MapPracticeEndpoints();
api.MapAnalysisEndpoints();
api.MapCardEndpoints();
api.MapUsageEndpoints();

app.Run();
