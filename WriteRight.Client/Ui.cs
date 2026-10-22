using System.Globalization;
using WriteRight.Shared;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Client;

/// <summary>
/// Textos de tela que mais de uma página usa. Um lugar só pra a mesma coisa não ser
/// dita de dois jeitos — a mensagem de erro, por exemplo, já existiu em duas versões.
/// </summary>
public static class Ui
{
    public static string LanguageName(Language language) => language switch
    {
        Language.Portuguese => "Português",
        Language.English => "Inglês",
        _ => language.ToString(),
    };

    /// <summary>O nome da gravidade — o mesmo no selo, na legenda e no tooltip dos gráficos.</summary>
    public static string SeverityLabel(ErrorSeverity severity) => severity switch
    {
        ErrorSeverity.BreaksMeaning => "Quebra o sentido",
        ErrorSeverity.Understandable => "Compreensível",
        ErrorSeverity.Polish => "Lapidação",
        _ => severity.ToString(),
    };

    /// <summary>Classe da cor da gravidade nos gráficos (a rampa <c>--wr-sev-*</c> do app.css).</summary>
    public static string SeverityFill(ErrorSeverity severity) => severity switch
    {
        ErrorSeverity.BreaksMeaning => "wr-sev--breaks",
        ErrorSeverity.Understandable => "wr-sev--understandable",
        _ => "wr-sev--polish",
    };

    /// <summary>
    /// A gravidade como frase depois de uma contagem: "3 <i>quebram o sentido</i>",
    /// "1 <i>compreensível</i>", "2 <i>de lapidação</i>".
    /// </summary>
    public static string SeverityPhrase(ErrorSeverity severity, int count) => severity switch
    {
        ErrorSeverity.BreaksMeaning => count == 1 ? "quebra o sentido" : "quebram o sentido",
        ErrorSeverity.Understandable => count == 1 ? "compreensível" : "compreensíveis",
        _ => "de lapidação",
    };

    /// <summary>"1 erro", "3 erros" — em vez do "erro(s)".</summary>
    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    public static readonly IReadOnlyList<string> MonthsShort =
        ["jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez"];

    /// <summary>"10 out" no ano corrente; "10 out 2025" fora dele, pra não confundir com o deste ano.</summary>
    public static string ShortDate(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var year = local.Year == DateTimeOffset.Now.Year ? "" : $" {local.Year}";
        return $"{local.Day} {MonthsShort[local.Month - 1]}{year}";
    }

    /// <summary>
    /// Uma fração (0–1) como porcentagem pra <c>style</c>, sempre com ponto decimal: no
    /// navegador em pt-BR, um "12,5%" quebra o CSS sem erro nenhum.
    /// </summary>
    public static string Pct(double fraction) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(fraction, 0, 1) * 100:0.###}%");

    /// <summary>
    /// A falha em palavras, separando os dois casos que pedem ações diferentes: a API
    /// fora do ar (sem status — o pedido nem chegou) e a API que respondeu com erro.
    /// Antes tudo virava "a API está rodando?", inclusive o 500 de uma correção que
    /// falhou com a API de pé.
    /// </summary>
    public static string ErrorMessage(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } =>
            "Não consegui falar com a API — ela está rodando (porta 5056)?",
        HttpRequestException { StatusCode: { } status } =>
            $"A API respondeu com erro ({(int)status}).",
        // O Timeout do HttpClient (Program.cs) chega como cancelamento, com mensagem em inglês.
        TaskCanceledException => "A API demorou demais pra responder.",
        _ => $"Algo deu errado: {ex.Message}",
    };
}
