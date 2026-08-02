namespace WriteRight.Api.Data;

/// <summary>
/// Entidade de persistência de uma análise de fraquezas gerada pela IA.
///
/// O corpo (padrões e itens de estudo) fica em JSON, não em tabelas filhas, de
/// propósito: é um <b>documento</b> — lido sempre inteiro, nunca consultado por
/// campo nem agregado. Modelar relacionalmente custaria quatro tabelas e uma
/// árvore de navegação pra ganhar zero consulta. O JSON guarda enums como string,
/// então continua legível no banco, na mesma linha do resto do schema.
///
/// A evidência dentro do JSON é <b>snapshot</b> do erro (texto copiado), não FK:
/// a análise é registro histórico e precisa continuar íntegra mesmo que a prática
/// de origem seja excluída depois.
/// </summary>
public class AnalysisRecord
{
    public int Id { get; set; }

    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Marca d'água: quantas práticas concluídas existiam quando isto foi gerado.</summary>
    public int PracticesAnalyzed { get; set; }

    /// <summary>Quantos erros reais foram efetivamente enviados ao modelo.</summary>
    public int ErrorsAnalyzed { get; set; }

    /// <summary>JSON de <c>AnalysisPattern[]</c> (já validado e hidratado).</summary>
    public string PatternsJson { get; set; } = "[]";

    /// <summary>JSON de <c>AnalysisStudyItem[]</c>.</summary>
    public string StudyItemsJson { get; set; } = "[]";
}
