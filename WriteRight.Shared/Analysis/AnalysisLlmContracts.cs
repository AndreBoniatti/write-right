using System.Collections.Generic;
using WriteRight.Shared.Profile;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Analysis;

/// <summary>
/// Uma linha de erro do jeito que o modelo a recebe. O <see cref="Id"/> é a chave
/// do contrato de evidência: o modelo cita ids, o servidor confere contra o conjunto
/// que enviou e descarta o que não existir. É o que impede afirmação sem lastro.
///
/// Só a linha do erro vai — nunca o texto inteiro da prática. Sem o texto, o modelo
/// não consegue afirmar nada que não possa apontar; a trava é estrutural, não retórica.
/// A <see cref="Explanation"/> é quem carrega o contexto (um "in → on" solto não diz
/// nada; "'on' se usa com dias da semana" entrega a sub-regra) — por isso ela vai
/// inteira, nunca truncada.
/// </summary>
public sealed record AnalysisErrorRow(
    int Id,
    ErrorCategory Category,
    ErrorSeverity Severity,
    string Original,
    string Correction,
    string Explanation);

/// <summary>
/// O que o modelo recebe pra diagnosticar: os erros reais da janela de análise mais
/// o agregado vitalício (barato, e dá o mapa do todo sem pagar o texto dos erros antigos).
/// </summary>
/// <param name="Errors">Erros reais da janela, das top categorias, do mais recente pro mais antigo.</param>
/// <param name="LifetimeByCategory">Agregado do histórico inteiro, por peso.</param>
/// <param name="PracticesAnalyzed">Quantas práticas a janela cobre.</param>
/// <param name="LifetimePractices">Quantas práticas concluídas existem ao todo.</param>
/// <param name="MaxPatterns">Teto de padrões na resposta.</param>
/// <param name="MinEvidence">Mínimo de erros citados pra um padrão ser aceito.</param>
/// <param name="MaxStudyItems">Teto de itens de estudo na resposta.</param>
public sealed record AnalysisRequest(
    IReadOnlyList<AnalysisErrorRow> Errors,
    IReadOnlyList<CategoryWeight> LifetimeByCategory,
    int PracticesAnalyzed,
    int LifetimePractices,
    int MaxPatterns,
    int MinEvidence,
    int MaxStudyItems);

/// <summary>
/// Padrão como o <b>modelo</b> devolve: evidência por id, ainda não validada nem
/// hidratada. Vira <see cref="AnalysisPattern"/> só depois de o servidor conferir os ids.
/// </summary>
public sealed record DraftPattern(
    string Title,
    string Diagnosis,
    IReadOnlyList<int> EvidenceErrorIds);

/// <summary>Saída crua do modelo, antes da validação de evidência.</summary>
public sealed record AnalysisDraft(
    IReadOnlyList<DraftPattern> Patterns,
    IReadOnlyList<AnalysisStudyItem> StudyItems);
