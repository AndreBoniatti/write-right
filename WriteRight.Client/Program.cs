using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WriteRight.Client;
using WriteRight.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Base da API vem do wwwroot/appsettings.json (ApiBaseUrl); default = API em dev.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5056";
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddScoped<WriteRightApiClient>();

await builder.Build().RunAsync();
