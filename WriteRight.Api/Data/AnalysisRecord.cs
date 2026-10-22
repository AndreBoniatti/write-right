namespace WriteRight.Api.Data;

/// <summary>
/// Entidade de persistência de uma análise de fraquezas gerada pela IA.
///
/// O corpo (padrões, cada um com o que estudar) fica em JSON, não em tabelas filhas, de
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

    /// <summary>O momento do retrato: quando a janela foi montada, não quando a IA respondeu.</summary>
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Marca d'água: quantas práticas a janela cobriu — as mais recentes, até juntar o
    /// orçamento de erros. Não é o total do histórico, que pode ser bem maior.
    /// </summary>
    public int PracticesAnalyzed { get; set; }

    /// <summary>Quantos erros reais foram efetivamente enviados ao modelo.</summary>
    public int ErrorsAnalyzed { get; set; }

    /// <summary>JSON de <c>AnalysisPattern[]</c> (já validado e hidratado).</summary>
    public string PatternsJson { get; set; } = "[]";
}
