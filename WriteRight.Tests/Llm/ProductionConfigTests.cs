using Microsoft.Extensions.Configuration;
using WriteRight.Api.Llm;

namespace WriteRight.Tests.Llm;

/// <summary>
/// Lê o <c>appsettings.json</c> REAL do projeto Api e o submete ao mesmo validador
/// que roda no startup.
///
/// Só passou a valer a pena quando o preço deixou de ter default no código: o arquivo
/// virou a fonte única, e um modelo mal grafado ali derruba a app no boot. Este teste
/// troca "descobre no deploy" por "descobre no <c>dotnet test</c>".
/// </summary>
public class ProductionConfigTests
{
    /// <summary>
    /// Sobe do diretório de saída dos testes até a raiz do repo (a que tem o .slnx) e
    /// localiza o appsettings do Api. Falha explícita se não achar — um teste que se
    /// auto-desliga quando o caminho muda não protege nada.
    /// </summary>
    private static string AppSettingsPath(string file = "appsettings.json")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WriteRight.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Raiz do repo (WriteRight.slnx) não encontrada a partir de " + AppContext.BaseDirectory);

        var path = Path.Combine(dir!.FullName, "WriteRight.Api", file);
        Assert.True(File.Exists(path), $"{file} não encontrado em {path}");
        return path;
    }

    private static IConfiguration Load(string file = "appsettings.json") =>
        new ConfigurationBuilder().AddJsonFile(AppSettingsPath(file), optional: false).Build();

    private static LlmOptions LoadLlmOptions(string file = "appsettings.json")
    {
        var options = new LlmOptions();
        Load(file).GetSection(LlmOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void Shipped_appsettings_passes_startup_validation()
    {
        var result = new LlmOptionsValidator().Validate(null, LoadLlmOptions());

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [Fact]
    public void Shipped_appsettings_prices_the_models_it_actually_uses()
    {
        var options = LoadLlmOptions();
        var pricing = new LlmPricing(Microsoft.Extensions.Options.Options.Create(options));

        // Um token de cada modelo em uso tem que produzir custo — não null.
        foreach (var model in new[] { options.GenerationModel, options.CorrectionModel, options.AnalysisModel })
            Assert.NotNull(pricing.CostOf(new LlmUsage(model, 1_000, 500, 0, 0)));
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void No_appsettings_file_carries_an_api_key(string file)
    {
        // Ambos são versionados. A key vem de user-secrets ou variável de ambiente.
        Assert.True(string.IsNullOrEmpty(LoadLlmOptions(file).ApiKey),
            $"Llm:ApiKey em {file}, que é versionado — mover pra user-secrets.");
    }

    [Fact]
    public void Development_does_not_redefine_pricing()
    {
        // Config é EMPILHADA: em Development o appsettings.json carrega primeiro e o
        // .Development.json sobrepõe por cima, chave a chave. Repetir a tabela de preço
        // lá não muda nada enquanto os valores forem iguais — e no dia em que alguém
        // corrigir só a base, dev passa a calcular custo com o preço velho, sem erro
        // nem aviso. Só entra no arquivo de ambiente o que precisa DIVERGIR.
        Assert.Empty(LoadLlmOptions("appsettings.Development.json").Pricing);
    }
}
