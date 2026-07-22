using System.Collections.Generic;
using System.Linq;

namespace WriteRight.Shared.Taxonomy;

/// <summary>Grupo pra organizar as categorias na UI.</summary>
public enum ErrorGroup
{
    Structure,
    Vocabulary,
    Mechanics,
    Style,
    Other,
}

/// <summary>
/// Metadados de uma categoria — fonte única da verdade, usada tanto pela UI
/// (labels/agrupamento) quanto pelo construtor do prompt de correção
/// (as descrições viram parte da instrução que a IA recebe).
/// </summary>
/// <param name="Category">A categoria.</param>
/// <param name="Group">Grupo de exibição.</param>
/// <param name="LabelPt">Nome amigável em PT, mostrado pro usuário.</param>
/// <param name="Description">O que é, em uma linha (entra no prompt da IA).</param>
/// <param name="Example">Exemplo curto do erro → correção.</param>
public sealed record ErrorCategoryInfo(
    ErrorCategory Category,
    ErrorGroup Group,
    string LabelPt,
    string Description,
    string Example);

/// <summary>
/// Catálogo canônico das categorias de erro. Ponto único consumido pela UI,
/// pelo prompt de correção e pelos relatórios do perfil.
/// </summary>
public static class ErrorCatalog
{
    /// <summary>Todas as categorias, na ordem de exibição.</summary>
    public static IReadOnlyList<ErrorCategoryInfo> All { get; } = new[]
    {
        // ── Estrutura / gramática ──
        new ErrorCategoryInfo(ErrorCategory.SubjectOmission, ErrorGroup.Structure,
            "Sujeito ausente",
            "Frase em inglês sem sujeito; o português omite, o inglês exige (mesmo vazio: 'it'/'there').",
            "\"Was a simple day\" → \"It was a simple day\""),
        new ErrorCategoryInfo(ErrorCategory.VerbTense, ErrorGroup.Structure,
            "Tempo verbal",
            "Tempo verbal errado pro contexto (passado / presente / futuro).",
            "\"When I arrive\" → \"When I arrived\""),
        new ErrorCategoryInfo(ErrorCategory.VerbForm, ErrorGroup.Structure,
            "Forma do verbo",
            "Forma/aspecto/voz errada: perfeito, contínuo, voz passiva, gerúndio vs infinitivo, modais.",
            "\"was prepared a lunch\" → \"had prepared a lunch\""),
        new ErrorCategoryInfo(ErrorCategory.Agreement, ErrorGroup.Structure,
            "Concordância",
            "Sujeito e verbo (ou substantivo e número) não concordam.",
            "\"grandparents that lives\" → \"grandparents who live\""),
        new ErrorCategoryInfo(ErrorCategory.Article, ErrorGroup.Structure,
            "Artigo",
            "Artigo (a / an / the) faltando, sobrando ou errado.",
            "\"I have car\" → \"I have a car\""),
        new ErrorCategoryInfo(ErrorCategory.Preposition, ErrorGroup.Structure,
            "Preposição",
            "Preposição errada ou faltando.",
            "\"excited with\" → \"excited about\""),
        new ErrorCategoryInfo(ErrorCategory.Pronoun, ErrorGroup.Structure,
            "Pronome",
            "Pronome errado, faltando, ou com caso errado.",
            "\"Me and him went\" → \"He and I went\""),
        new ErrorCategoryInfo(ErrorCategory.WordOrder, ErrorGroup.Structure,
            "Ordem das palavras",
            "Ordem incorreta (adjetivo-substantivo, advérbio, estrutura de pergunta).",
            "\"a car red\" → \"a red car\""),
        new ErrorCategoryInfo(ErrorCategory.NumberCountability, ErrorGroup.Structure,
            "Plural / contável",
            "Erro de plural, singular ou contável/incontável.",
            "\"many informations\" → \"much information\""),
        new ErrorCategoryInfo(ErrorCategory.MissingOrExtraWord, ErrorGroup.Structure,
            "Palavra faltando ou sobrando",
            "Palavra necessária omitida ou desnecessária inserida (fora dos casos de artigo/sujeito).",
            "\"I discussed about it\" → \"I discussed it\""),

        // ── Vocabulário / sentido ──
        new ErrorCategoryInfo(ErrorCategory.WordChoice, ErrorGroup.Vocabulary,
            "Escolha de palavra",
            "A palavra existe, mas tem o sentido errado pro contexto.",
            "\"on the farm\" → \"in the countryside\""),
        new ErrorCategoryInfo(ErrorCategory.FalseCognate, ErrorGroup.Vocabulary,
            "Falso cognato",
            "Palavra parecida com o português, mas de sentido diferente.",
            "\"actually\" (≠ atualmente) → \"currently\""),
        new ErrorCategoryInfo(ErrorCategory.Collocation, ErrorGroup.Vocabulary,
            "Colocação",
            "Palavras certas, mas que não combinam naturalmente.",
            "\"make a party\" → \"throw a party\""),
        new ErrorCategoryInfo(ErrorCategory.LiteralTranslation, ErrorGroup.Vocabulary,
            "Tradução literal",
            "Estrutura/expressão do português traduzida ao pé da letra.",
            "\"than I stayed\" (de 'então fiquei') → \"so I was\""),

        // ── Mecânica ──
        new ErrorCategoryInfo(ErrorCategory.Spelling, ErrorGroup.Mechanics,
            "Ortografia",
            "Palavra escrita errada.",
            "\"remamber\" → \"remember\""),
        new ErrorCategoryInfo(ErrorCategory.Capitalization, ErrorGroup.Mechanics,
            "Maiúsculas",
            "Uso errado de maiúscula/minúscula.",
            "\"i went\" → \"I went\""),
        new ErrorCategoryInfo(ErrorCategory.Punctuation, ErrorGroup.Mechanics,
            "Pontuação",
            "Pontuação errada ou faltando (vírgula, apóstrofo…).",
            "\"its raining\" → \"it's raining\""),

        // ── Estilo ──
        new ErrorCategoryInfo(ErrorCategory.Naturalness, ErrorGroup.Style,
            "Naturalidade",
            "Gramaticalmente correto, mas não é como um nativo diria.",
            "\"It's raining very much\" → \"It's raining a lot\""),

        // ── Escape ──
        new ErrorCategoryInfo(ErrorCategory.Other, ErrorGroup.Other,
            "Outro",
            "Não se encaixa em nenhuma categoria acima. Usar só em último caso (taxa alta = falta categoria).",
            "—"),
    };

    /// <summary>Lookup por categoria.</summary>
    public static IReadOnlyDictionary<ErrorCategory, ErrorCategoryInfo> ByCategory { get; } =
        All.ToDictionary(i => i.Category);

    /// <summary>Metadados de uma categoria.</summary>
    public static ErrorCategoryInfo Info(ErrorCategory category) => ByCategory[category];
}
