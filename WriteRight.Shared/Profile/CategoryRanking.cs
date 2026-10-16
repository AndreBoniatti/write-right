using System.Collections.Generic;
using System.Linq;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Profile;

/// <summary>
/// A régua que ordena categorias de erro: peso por gravidade primeiro, volume depois,
/// e o enum como desempate final pra ordem não depender da ordem de chegada.
///
/// Uma regra só porque ela aparece em três lugares que o usuário compara entre si — o
/// perfil, o material que vai pra análise e o resumo da correção. Duas cópias da
/// mesma cadeia de LINQ acabariam divergindo, e o "seu ponto mais fraco" mudaria de
/// tela pra tela.
/// </summary>
public static class CategoryRanking
{
    public static IReadOnlyList<CategoryWeight> Rank(
        IEnumerable<(ErrorCategory Category, ErrorSeverity Severity)> errors) =>
        errors
            .GroupBy(e => e.Category)
            .Select(g => new CategoryWeight(g.Key, g.Count(), g.Sum(e => SeverityWeight.Of(e.Severity))))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Count)
            .ThenBy(c => c.Category)
            .ToList();
}
