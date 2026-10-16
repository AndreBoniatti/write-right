using WriteRight.Shared;

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
