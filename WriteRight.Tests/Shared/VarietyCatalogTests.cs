using WriteRight.Shared.Exercises;

namespace WriteRight.Tests.Shared;

/// <summary>
/// O sorteio é a única fonte de variedade da geração — o modelo é sem memória entre
/// chamadas, então se isto colapsar, todo texto volta a sair igual. Os testes travam
/// as duas propriedades que importam: os valores vêm sempre do catálogo, e o sorteio
/// de fato espalha.
/// </summary>
public class VarietyCatalogTests
{
    [Fact]
    public void Pick_only_returns_values_from_the_catalog()
    {
        for (var i = 0; i < 200; i++)
        {
            var variety = VarietyCatalog.Pick();

            Assert.Contains(variety.Domain, VarietyCatalog.Domains);
            Assert.Contains(variety.Tense, VarietyCatalog.Tenses);
            Assert.Contains(variety.Register, VarietyCatalog.Registers);
            Assert.Contains(variety.PointOfView, VarietyCatalog.PointsOfView);
        }
    }

    [Fact]
    public void Pick_spreads_across_every_axis()
    {
        var picks = Enumerable.Range(0, 200).Select(_ => VarietyCatalog.Pick()).ToList();

        // Com 200 sorteios, um eixo travado num valor só é falha real, não azar:
        // o menor catálogo tem 3 opções, então P(tudo igual) ≈ 3 × (1/3)^200.
        Assert.True(picks.Select(v => v.Domain).Distinct().Count() > 1);
        Assert.True(picks.Select(v => v.Tense).Distinct().Count() > 1);
        Assert.True(picks.Select(v => v.Register).Distinct().Count() > 1);
        Assert.True(picks.Select(v => v.PointOfView).Distinct().Count() > 1);

        // E a letra do personagem também precisa girar — era o vício do "Marina".
        var initials = picks.Where(v => v.CharacterInitial is not null)
            .Select(v => v.CharacterInitial!.Value).Distinct().ToList();
        Assert.True(initials.Count > 1);
        Assert.All(initials, c => Assert.Contains(c, VarietyCatalog.CharacterInitials));
    }

    [Fact]
    public void Character_initial_comes_only_with_a_named_character()
    {
        var picks = Enumerable.Range(0, 200).Select(_ => VarietyCatalog.Pick()).ToList();

        foreach (var variety in picks)
        {
            var named = variety.PointOfView.Contains("personagem nomeado");
            Assert.Equal(named, variety.CharacterInitial is not null);
        }

        // Os três pontos de vista aparecem, então os dois lados foram exercitados.
        Assert.Equal(
            VarietyCatalog.PointsOfView.OrderBy(p => p),
            picks.Select(v => v.PointOfView).Distinct().OrderBy(p => p));
    }

    [Fact]
    public void Pick_is_reproducible_for_a_given_seed()
    {
        // Semente fixa = mesmo sorteio. Serve pra depurar "por que saiu este texto".
        Assert.Equal(VarietyCatalog.Pick(new Random(42)), VarietyCatalog.Pick(new Random(42)));
    }

    [Fact]
    public void Domain_catalog_is_wide_and_drops_the_old_funnel()
    {
        // O fallback antigo era "um tema cotidiano" — um funil que puxava tudo pra
        // vinheta doméstica. A largura da lista é o que substitui aquilo.
        Assert.True(VarietyCatalog.Domains.Count >= 40);
        Assert.DoesNotContain(VarietyCatalog.Domains, d => d.Contains("cotidiano"));
    }

    [Fact]
    public void Catalogs_have_no_duplicates()
    {
        // Lista mantida à mão: item repetido não quebra nada, só enviesa o sorteio
        // em silêncio — o assunto duplicado sai com o dobro da frequência.
        Assert.Equal(VarietyCatalog.Domains.Count, VarietyCatalog.Domains.Distinct().Count());
        Assert.Equal(VarietyCatalog.Tenses.Count, VarietyCatalog.Tenses.Distinct().Count());
        Assert.Equal(VarietyCatalog.Registers.Count, VarietyCatalog.Registers.Distinct().Count());
        Assert.Equal(
            VarietyCatalog.CharacterInitials.Count,
            VarietyCatalog.CharacterInitials.Distinct().Count());
    }
}
