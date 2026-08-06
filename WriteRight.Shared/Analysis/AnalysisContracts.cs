using System;
using System.Collections.Generic;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Analysis;

/// <summary>
/// Como um item de estudo se apresenta. A distinção existe porque nem todo
/// problema cabe num parágrafo: regra fechada dá pra ensinar ali; habilidade
/// ampla só dá pra apontar — fingir o contrário seria vender fumaça.
/// </summary>
public enum StudyItemKind
{
    /// <summary>Regra fechada, explicada inline (in/on/at de tempo, artigo com incontáveis…).</summary>
    Rule,

    /// <summary>Habilidade ampla que exige volume de exposição; só se aponta o quê e o porquê.</summary>
    Topic,
}

/// <summary>
/// Um erro real seu, citado como evidência de um padrão. É <b>snapshot</b>: o texto
/// fica gravado dentro da análise, não é ponteiro pro <c>ExerciseError</c>. Assim o
/// diagnóstico continua íntegro e legível mesmo que a prática de origem seja excluída.
/// </summary>
public sealed record AnalysisEvidence(
    int PracticeId,
    ErrorCategory Category,
    ErrorSeverity Severity,
    string Original,
    string Correction,
    string Explanation);

/// <summary>
/// Um padrão encontrado no seu histórico — o que a taxonomia sozinha não enxerga
/// (a sub-regra dentro da categoria, ou o fio que liga categorias diferentes).
///
/// <see cref="Categories"/> é <b>derivada</b> da evidência, não pedida ao modelo:
/// menos uma superfície pra ele errar. Todo padrão nasce com evidência real —
/// sem isso ele é descartado no servidor antes de virar registro.
/// </summary>
public sealed record AnalysisPattern(
    string Title,
    string Diagnosis,
    IReadOnlyList<ErrorCategory> Categories,
    IReadOnlyList<AnalysisEvidence> Evidence);

/// <summary>
/// O que estudar a seguir. <see cref="StudyItemKind.Rule"/> traz a regra explicada
/// em <see cref="Content"/>; <see cref="StudyItemKind.Topic"/> traz o porquê e o que
/// procurar.
/// </summary>
public sealed record AnalysisStudyItem(
    StudyItemKind Kind,
    string Title,
    string Content);

/// <summary>
/// Uma análise gerada e persistida: diagnóstico do estado atual, com a marca d'água
/// do que ele viu (<see cref="PracticesAnalyzed"/> / <see cref="ErrorsAnalyzed"/>)
/// pra você saber sobre quanto material a conclusão se apoia.
/// </summary>
public sealed record WeaknessAnalysis(
    int Id,
    DateTimeOffset GeneratedAt,
    int PracticesAnalyzed,
    int ErrorsAnalyzed,
    IReadOnlyList<AnalysisPattern> Patterns,
    IReadOnlyList<AnalysisStudyItem> StudyItems);

/// <summary>Por que o botão de gerar está (ou não) disponível.</summary>
public enum AnalysisGate
{
    /// <summary>Dá pra gerar: há material suficiente e novidade desde a última.</summary>
    Ready,

    /// <summary>Histórico pequeno demais — qualquer padrão seria ruído com cara de conclusão.</summary>
    NotEnoughData,

    /// <summary>Já existe análise e quase nada mudou desde então; regerar só gastaria.</summary>
    UpToDate,
}

/// <summary>Situação da geração em background.</summary>
public enum AnalysisJobStatus
{
    /// <summary>Nada rodando.</summary>
    Idle,

    /// <summary>Em execução no servidor. O cliente acompanha por polling.</summary>
    Running,

    /// <summary>A última execução falhou; o motivo está em <see cref="AnalysisJob.FailureReason"/>.</summary>
    Failed,
}

/// <summary>
/// Estado da geração em background.
///
/// A análise é a chamada mais lenta do app e não cabe no ciclo de uma requisição
/// HTTP: navegador, proxy e balanceador têm timeouts próprios que você não controla
/// (o free do Cloudflare corta em 100s). Então o <c>POST</c> só enfileira, e é este
/// objeto — devolvido pelo <c>GET</c> — que diz em que pé está.
/// </summary>
/// <param name="FailureReason">Mensagem pro usuário quando <see cref="AnalysisJobStatus.Failed"/>.</param>
/// <param name="StartedAt">Quando a execução em curso começou.</param>
public sealed record AnalysisJob(
    AnalysisJobStatus Status,
    string? FailureReason,
    DateTimeOffset? StartedAt)
{
    public static AnalysisJob Idle { get; } = new(AnalysisJobStatus.Idle, null, null);

    public static AnalysisJob Started(DateTimeOffset at) => new(AnalysisJobStatus.Running, null, at);

    public static AnalysisJob Failure(string reason) => new(AnalysisJobStatus.Failed, reason, null);
}

/// <summary>
/// Estado da tela de análise: a última análise (se houver) e se vale gerar outra.
/// Os contadores vêm juntos pra UI explicar o motivo em vez de só desabilitar o botão.
/// </summary>
/// <param name="Latest">A análise mais recente, ou null se nunca gerou.</param>
/// <param name="Gate">Se dá pra gerar agora, e por quê não.</param>
/// <param name="CompletedPractices">Práticas concluídas no histórico todo.</param>
/// <param name="TotalErrors">Erros no histórico todo.</param>
/// <param name="NewPracticesSinceLatest">Práticas concluídas depois da última análise.</param>
/// <param name="MinPractices">Piso de práticas pra habilitar a geração.</param>
/// <param name="MinErrors">Piso de erros pra habilitar a geração.</param>
/// <param name="Job">Se há geração rodando agora (ou se a última falhou).</param>
public sealed record AnalysisState(
    WeaknessAnalysis? Latest,
    AnalysisGate Gate,
    int CompletedPractices,
    int TotalErrors,
    int NewPracticesSinceLatest,
    int MinPractices,
    int MinErrors,
    AnalysisJob Job);
