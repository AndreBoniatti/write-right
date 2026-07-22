using System.Collections.Generic;

namespace WriteRight.Shared.Corrections;

/// <summary>
/// Pedido de correção: o texto original + a tradução que o usuário escreveu.
/// A correção avalia a escrita no idioma-alvo (<see cref="TargetLanguage"/>).
/// </summary>
public sealed record CorrectionRequest(
    Language SourceLanguage,
    Language TargetLanguage,
    string SourceText,
    string UserTranslation,
    CefrLevel? Level = null,
    string? Theme = null);

/// <summary>
/// Resultado da correção — objeto que a IA devolve (structured output) e que
/// a API entrega pro front. <see cref="Errors"/> é o que alimenta o perfil.
/// </summary>
/// <param name="CorrectedText">A tradução corrigida por inteiro (no idioma-alvo).</param>
/// <param name="Errors">Lista de erros categorizados.</param>
/// <param name="OverallComment">Comentário geral/encorajador em português.</param>
public sealed record CorrectionResult(
    string CorrectedText,
    IReadOnlyList<WritingError> Errors,
    string OverallComment);
