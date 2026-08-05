using WriteRight.Shared.Usage;

namespace WriteRight.Api.Data;

/// <summary>
/// Entidade de persistência: UMA chamada à IA e o que ela consumiu.
///
/// É tabela própria, e não colunas em <see cref="ExerciseAttempt"/>, por três
/// motivos concretos:
///  • Uma prática faz DUAS chamadas em momentos diferentes (geração na criação,
///    correção depois) — em colunas viraria oito campos e um mapeamento torto.
///  • Chamada que gastou e não produziu registro existe: a análise sem lastro
///    (<c>NoGrounding</c>) não persiste <see cref="AnalysisRecord"/> nenhum, mas
///    foi cobrada. Em coluna, esse custo sumiria — e é justamente o que interessa ver.
///  • Cota, depois, é <c>SUM(custo) WHERE usuário E período</c>: uma tabela, uma
///    query, independente da operação.
///
/// <see cref="PracticeId"/> e <see cref="AnalysisId"/> são referências FRACAS
/// (int simples, sem FK e sem cascade), pelo mesmo motivo que a evidência da
/// análise é snapshot: o gasto aconteceu e é irreversível — excluir a prática não
/// pode apagar o registro de que você pagou por ela.
/// </summary>
public class LlmCall
{
    public int Id { get; set; }

    public LlmOperation Operation { get; set; }

    /// <summary>Modelo efetivamente usado (vem da resposta, não da config — é o que foi cobrado).</summary>
    public string Model { get; set; } = "";

    /// <summary>Entrada NÃO cacheada. Os quatro baldes de token não se sobrepõem.</summary>
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long CacheReadTokens { get; set; }

    /// <summary>
    /// Custo calculado NA HORA da chamada (retrato). Null = modelo fora da tabela
    /// de preços; os tokens continuam válidos e o custo dá pra recalcular depois.
    /// </summary>
    public decimal? CostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Prática que originou a chamada (geração/correção). Referência fraca — ver resumo da classe.</summary>
    public int? PracticeId { get; set; }

    /// <summary>Análise que a chamada produziu. Null também quando a análise não teve lastro e nada foi persistido.</summary>
    public int? AnalysisId { get; set; }
}
