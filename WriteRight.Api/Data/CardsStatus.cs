namespace WriteRight.Api.Data;

/// <summary>
/// Em que pé está a cunhagem dos cards de uma prática.
///
/// Existe porque a cunhagem roda FORA da requisição de correção, e uma falha ali é
/// invisível por natureza: ninguém pediu os cards, então ninguém percebe que eles não
/// nasceram. A fila da análise pode ser em memória porque, se ela se perder, a tela
/// volta pra "pedir de novo"; aqui não existe esse "de novo". O status no banco é o
/// que transforma um restart no meio do caminho em "retoma no próximo boot" em vez de
/// "perde em silêncio".
///
/// Null na prática significa "não se aplica": prática em andamento, ou corrigida antes
/// de este passo existir.
/// </summary>
public enum CardsStatus
{
    /// <summary>Corrigida, cards ainda por fazer. Gravado na MESMA transação da correção.</summary>
    Pending,

    /// <summary>Cunhagem concluída — inclusive quando nenhum erro virou card.</summary>
    Done,

    /// <summary>Falhou em todas as tentativas. Só volta pra fila por pedido explícito.</summary>
    Failed,
}
