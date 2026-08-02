using System.Collections.Generic;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Exercises;

/// <summary>
/// Parâmetros pra gerar um exercício. O texto é produzido em
/// <see cref="SourceLanguage"/> e o usuário traduz pra <see cref="TargetLanguage"/>.
/// </summary>
/// <param name="SourceLanguage">Idioma do texto gerado (o que o usuário lê).</param>
/// <param name="TargetLanguage">Idioma-alvo da tradução (o que o usuário escreve).</param>
/// <param name="WordCount">Tamanho aproximado do texto, em palavras.</param>
/// <param name="Level">Nível de dificuldade (CEFR).</param>
/// <param name="Theme">Tema livre (ex.: "viagem", "trabalho"). Nulo = a IA escolhe.</param>
/// <param name="FocusCategories">
/// Gancho adaptativo: categorias que o texto deve <b>forçar</b> o usuário a
/// praticar (as fraquezas do perfil). Nulo/vazio = texto neutro.
/// </param>
/// <param name="Variety">
/// Eixos de forma sorteados no servidor (tempo verbal, registro, ponto de vista,
/// assunto). É o que impede a geração de convergir sempre pro mesmo texto — ver
/// <see cref="TextVariety"/>. Nulo = sem variação forçada.
/// </param>
public sealed record ExerciseGenerationRequest(
    Language SourceLanguage,
    Language TargetLanguage,
    int WordCount,
    CefrLevel Level,
    string? Theme = null,
    IReadOnlyList<ErrorCategory>? FocusCategories = null,
    TextVariety? Variety = null);

/// <summary>Texto gerado pro usuário traduzir, com o eco dos parâmetros usados.</summary>
public sealed record GeneratedExercise(
    Language SourceLanguage,
    Language TargetLanguage,
    string SourceText,
    CefrLevel Level,
    string? Theme);
