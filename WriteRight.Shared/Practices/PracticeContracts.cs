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
/// Detalhe completo de uma prática — abrir/retomar (InProgress) ou ler (Completed).
/// Correção só vem preenchida quando concluída.
/// </summary>
/// <param name="MintedCards">
/// Cards de vocabulário que ESTA correção acabou de gerar. Vem preenchido só na
/// resposta da correção, e null na leitura — a contagem é um fato do instante da
/// cunhagem, não um atributo da prática. Inventar um número na releitura (contando
/// erros de vocabulário, digamos) mentiria: nem todo erro vira card.
/// </param>
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
    DateTimeOffset? CompletedAt,
    int? MintedCards = null);
