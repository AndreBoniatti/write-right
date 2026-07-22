using WriteRight.Shared;

namespace WriteRight.Api.Llm;

/// <summary>Textos auxiliares compartilhados pelos construtores de prompt.</summary>
internal static class LlmText
{
    /// <summary>Nome do idioma em português, pra usar dentro dos prompts.</summary>
    public static string LanguageName(Language lang) => lang switch
    {
        Language.Portuguese => "português",
        Language.English => "inglês",
        _ => lang.ToString(),
    };
}
