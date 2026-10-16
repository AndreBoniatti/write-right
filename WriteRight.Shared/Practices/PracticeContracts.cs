using System;
using System.Collections.Generic;
using WriteRight.Shared.Corrections;

namespace WriteRight.Shared.Practices;

/// <summary>
/// Cria uma prática = gera o texto e persiste como <see cref="PracticeStatus.InProgress"/>.
/// O direcionamento adaptativo é resolvido no servidor: com
/// <see cref="FocusOnWeaknesses"/>, o backend puxa as categorias mais frequentes do
/// perfil e mira a geração nelas (o cliente não precisa saber do perfil).
/// </summary>
public sealed record CreatePracticeRequest(
    Language SourceLanguage,
    Language TargetLanguage,
    int WordCount,
    CefrLevel Level,
    string? Theme = null,
    bool FocusOnWeaknesses = false);

/// <summary>
/// Tamanho aceito pro texto gerado, em palavras. No Shared porque a tela e o servidor
/// aplicam a mesma régua: o <c>min</c>/<c>max</c> do campo não impede digitar 2000, e
/// o servidor é quem recusa antes da chamada paga. Muito acima do teto, a resposta da
/// geração estoura o limite de saída e chega cortada — cobrada e inútil. O piso é baixo
/// de propósito: texto curto é barato, e há práticas reais de 8 palavras; ele só recusa
/// pedido sem sentido.
/// </summary>
public static class PracticeLimits
{
    public const int MinWords = 10;
    public const int MaxWords = 200;
}

/// <summary>
/// A tradução que o usuário submete — serve tanto pra salvar rascunho
/// ("Salvar e sair") quanto pra corrigir (concluir a prática).
/// </summary>
public sealed record PracticeTranslationRequest(string UserTranslation);

/// <summary>
/// Item da listagem (tela inicial): resumo leve, sem os textos completos nem os erros.
/// </summary>
public sealed record PracticeSummary(
    int Id,
    Language SourceLanguage,
    Language TargetLanguage,
    CefrLevel? Level,
    string? Theme,
    PracticeStatus Status,
    string Preview,
    int ErrorCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// Uma página da listagem. <see cref="Total"/> é o tamanho da lista inteira: o
/// cliente sabe se ainda há o que carregar comparando com quantas já tem.
/// </summary>
public sealed record PracticePage(IReadOnlyList<PracticeSummary> Items, int Total);

/// <summary>
/// Detalhe completo de uma prática — abrir/retomar (InProgress) ou ler (Completed).
/// Correção só vem preenchida quando concluída.
/// </summary>
public sealed record PracticeDetail(
    int Id,
    Language SourceLanguage,
    Language TargetLanguage,
    CefrLevel? Level,
    string? Theme,
    PracticeStatus Status,
    string SourceText,
    string UserTranslation,
    string? CorrectedText,
    IReadOnlyList<WritingError> Errors,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
