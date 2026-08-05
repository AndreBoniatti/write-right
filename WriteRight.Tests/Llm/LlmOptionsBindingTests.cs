using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WriteRight.Api.Llm;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Llm;

/// <summary>
/// Preço mora SÓ no appsettings, então o caminho <c>appsettings → LlmOptions →
/// LlmPricing</c> é funcionalidade, não infra: se o bind quebrar, nenhum teste de
/// unidade com objeto na mão percebe — o app segue rodando e só o custo fica errado.
///
/// Aqui se exercita o binder de verdade, porque os pontos que falham são justamente
/// os que objeto na mão não pega: chave de dicionário com hífen
/// (<c>claude-sonnet-5</c>) e <c>decimal</c> vindo de string.
/// </summary>
public class LlmOptionsBindingTests
{
    /// <summary>Monta a config como o appsettings faria (chave achatada com ':').</summary>
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static LlmOptions Bind(IConfiguration config)
    {
        var options = new LlmOptions();
        config.GetSection(LlmOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void Pricing_binds_a_model_key_containing_hyphens()
    {
        // O nome do modelo tem hífen e vira CHAVE de dicionário. É o ponto mais
        // provável de o bind falhar em silêncio.
        var options = Bind(Config(
            ("Llm:Pricing:claude-sonnet-5:InputPerMTok", "2.00"),
            ("Llm:Pricing:claude-sonnet-5:OutputPerMTok", "10.00")));

        Assert.True(options.Pricing.ContainsKey("claude-sonnet-5"));
        Assert.Equal(2.00m, options.Pricing["claude-sonnet-5"].InputPerMTok);
        Assert.Equal(10.00m, options.Pricing["claude-sonnet-5"].OutputPerMTok);
    }

    [Fact]
    public void Configured_rate_reaches_LlmPricing_and_produces_the_cost()
    {
        // Ponta a ponta: config → options → custo calculado.
        var options = Bind(Config(
            ("Llm:Pricing:claude-sonnet-5:InputPerMTok", "2.00"),
            ("Llm:Pricing:claude-sonnet-5:OutputPerMTok", "10.00")));

        // (1000×2 + 500×10) ÷ 1M = 0,007
        Assert.Equal(0.007m, TestPricing.From(options).CostOf(
            new LlmUsage("claude-sonnet-5", 1_000, 500, 0, 0)));
    }

    [Fact]
    public void Empty_config_still_yields_the_default_models_but_no_prices()
    {
        // Modelo tem default no código (é identificador, não dinheiro). Preço não tem.
        var options = Bind(Config());

        Assert.Equal("claude-haiku-4-5", options.GenerationModel);
        Assert.Equal("claude-sonnet-5", options.CorrectionModel);
        Assert.Empty(options.Pricing);
    }
}

/// <summary>
/// Sem tarifa embutida no código, esquecer um preço não quebraria nada visível: a app
/// subiria, as práticas funcionariam, e só o relatório de custo viria vazio. Estes
/// testes travam a conversão desse silêncio em falha de boot.
/// </summary>
public class LlmOptionsValidatorTests
{
    private static readonly LlmOptionsValidator Validator = new();

    /// <summary>Options válidas: os três modelos em uso com tarifa.</summary>
    private static LlmOptions Valid()
    {
        var options = new LlmOptions();
        options.Pricing["claude-haiku-4-5"] = new ModelRate { InputPerMTok = 1m, OutputPerMTok = 5m };
        options.Pricing["claude-sonnet-5"] = new ModelRate { InputPerMTok = 3m, OutputPerMTok = 15m };
        return options;
    }

    [Fact]
    public void Accepts_options_where_every_model_in_use_has_a_rate()
    {
        Assert.True(Validator.Validate(null, Valid()).Succeeded);
    }

    [Fact]
    public void Rejects_empty_pricing()
    {
        // O caso que motivou a trava: appsettings sem a seção Llm:Pricing.
        var result = Validator.Validate(null, new LlmOptions());

        Assert.True(result.Failed);
        Assert.Contains("não tem tarifa", string.Join(" ", result.Failures!));
    }

    [Fact]
    public void Rejects_a_model_in_use_that_has_no_rate()
    {
        var options = Valid();
        options.CorrectionModel = "claude-modelo-novo"; // trocou o modelo, esqueceu o preço

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        // A mensagem tem que dizer QUAL modelo e como corrigir — ela é a UX da falha.
        var message = string.Join(" ", result.Failures!);
        Assert.Contains("CorrectionModel", message);
        Assert.Contains("claude-modelo-novo", message);
        Assert.Contains("InputPerMTok", message);
    }

    [Fact]
    public void Rejects_a_negative_rate()
    {
        var options = Valid();
        options.Pricing["claude-sonnet-5"] = new ModelRate { InputPerMTok = -3m, OutputPerMTok = 15m };

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("negativa", string.Join(" ", result.Failures!));
    }

    [Fact]
    public void Accepts_a_zero_rate()
    {
        // Zero é afirmação explícita de que o modelo não custa; ausência é que é erro.
        var options = Valid();
        options.Pricing["claude-sonnet-5"] = new ModelRate { InputPerMTok = 0m, OutputPerMTok = 0m };

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Matches_the_rate_key_case_insensitively()
    {
        // O LlmPricing busca ignorando maiúscula; se aqui fosse case-sensitive, a app
        // não subiria por uma tarifa que na prática FUNCIONA.
        var options = new LlmOptions();
        options.Pricing["Claude-Haiku-4-5"] = new ModelRate { InputPerMTok = 1m, OutputPerMTok = 5m };
        options.Pricing["CLAUDE-SONNET-5"] = new ModelRate { InputPerMTok = 3m, OutputPerMTok = 15m };

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void ValidateOnStart_wires_the_validator_into_host_startup()
    {
        // Prova a fiação: monta o container como o Program monta e roda o
        // IStartupValidator, que é exatamente o que o host executa ao subir.
        var services = new ServiceCollection();
        services.AddSingleton<IValidateOptions<LlmOptions>, LlmOptionsValidator>();
        services.AddOptions<LlmOptions>().ValidateOnStart(); // sem Bind → Pricing vazio

        var validator = services.BuildServiceProvider().GetRequiredService<IStartupValidator>();

        Assert.Throws<OptionsValidationException>(() => validator.Validate());
    }
}
