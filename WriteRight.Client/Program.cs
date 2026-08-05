using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WriteRight.Client;
using WriteRight.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Base da API vem do wwwroot/appsettings.json (ApiBaseUrl); default = API em dev.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5056";

// O default do HttpClient é 100s — curto demais aqui. A análise lê o histórico
// inteiro de erros e o modelo pode passar disso com folga; a correção de um texto
// longo também. E desistir é caro: a chamada à IA segue até o fim do lado do servidor,
// então o timeout do cliente não economiza nada, só esconde do usuário um resultado
// que já foi pago (ele fica no banco e aparece ao recarregar).
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(apiBaseUrl),
    Timeout = TimeSpan.FromMinutes(5),
});
builder.Services.AddScoped<WriteRightApiClient>();

await builder.Build().RunAsync();
