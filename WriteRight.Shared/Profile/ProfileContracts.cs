using System.Collections.Generic;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Profile;

/// <summary>Quantas vezes uma categoria de erro apareceu no histórico do usuário.</summary>
public sealed record CategoryCount(ErrorCategory Category, int Count);

/// <summary>
/// Perfil de fraquezas do usuário — agregação de todos os erros já cometidos.
/// É o insumo do gerador adaptativo (mira as categorias mais frequentes).
/// </summary>
/// <param name="TotalAttempts">Quantos exercícios foram corrigidos.</param>
/// <param name="TotalErrors">Total de erros acumulados.</param>
/// <param name="ByCategory">Contagem por categoria, da mais frequente pra menos.</param>
public sealed record ErrorProfile(
    int TotalAttempts,
    int TotalErrors,
    IReadOnlyList<CategoryCount> ByCategory);
