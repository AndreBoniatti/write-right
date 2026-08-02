using System;
using System.Collections.Generic;

namespace WriteRight.Shared.Exercises;

/// <summary>
/// Eixos de forma sorteados a cada geração. Existe por um motivo específico: cada
/// chamada ao modelo é independente e sem memória — ele não sabe o que já escreveu
/// antes. Com o mesmo prompt, a resposta cai sempre no mesmo modo (narrativa em
/// passado, personagem com o mesmo nome, mesma cena). Se a variedade não pode vir
/// do modelo, ela vem daqui: sorteamos a forma e mandamos junto.
///
/// São strings, não enums, de propósito: nada no sistema ramifica sobre estes
/// valores nem os persiste — eles só são concatenados no prompt. Enum aqui seria
/// cerimônia sem ganho (diferente da taxonomia de erros, que move pontuação e UI).
/// </summary>
/// <param name="Domain">Assunto sorteado, usado quando o usuário não escolheu tema.</param>
/// <param name="Tense">Tempo verbal predominante.</param>
/// <param name="Register">Registro do texto (narrativo, expositivo…).</param>
/// <param name="PointOfView">Pessoa / presença de personagem.</param>
/// <param name="CharacterInitial">
/// Letra inicial do personagem, só quando o ponto de vista pede um nome. Sortear a
/// LETRA em vez do nome mantém isto independente de idioma — o modelo escolhe um
/// nome que soe natural na língua do texto, mas não pode cair sempre no mesmo.
/// </param>
public sealed record TextVariety(
    string Domain,
    string Tense,
    string Register,
    string PointOfView,
    char? CharacterInitial);

/// <summary>
/// As listas de onde a <see cref="TextVariety"/> é sorteada. Larga de propósito:
/// o fallback antigo era "um tema cotidiano", que é um funil estreito — vinheta
/// doméstica em pretérito é o centro exato dessa distribuição.
/// </summary>
public static class VarietyCatalog
{
    /// <summary>
    /// Assuntos — <b>pontos de partida</b>, não cercas: o prompt manda o modelo
    /// derivar a partir daqui, senão os textos ficariam presos ao literal da lista.
    ///
    /// A largura importa mais que o tamanho. Um item novo só vale se abrir território
    /// que nenhum outro cobre; mais uma variação de "vida prática" quase não move a
    /// agulha. Por isso a lista mistura concreto (transporte, consertos) com abstrato
    /// (justiça, gerações), imaginativo (lendas) e autorreferente (idiomas).
    /// </summary>
    public static IReadOnlyList<string> Domains { get; } = new[]
    {
        "transporte público e deslocamento na cidade",
        "clima, estações do ano e fenômenos naturais",
        "esporte, treino e competição",
        "tecnologia e aparelhos do dia a dia",
        "trabalho e ambiente profissional",
        "estudo, escola e aprendizado",
        "saúde, sono e hábitos",
        "viagem, turismo e hospedagem",
        "comida, mercado e culinária",
        "música e instrumentos",
        "cinema, séries e leitura",
        "animais e natureza",
        "cidade, bairros e arquitetura",
        "compras, preços e dinheiro",
        "burocracia e serviços (banco, correio, documentos)",
        "história e acontecimentos do passado",
        "ciência e descobertas",
        "artesanato e trabalho manual",
        "vizinhança e convivência",
        "planos, metas e decisões",
        "mudança de casa ou de cidade",
        "consertos e problemas práticos",
        "memórias de infância e lembranças antigas",
        "tradições, festas e celebrações",
        "jogos de tabuleiro e videogames",
        "internet, redes sociais e vida online",
        "meio ambiente, lixo e sustentabilidade",
        "astronomia e exploração espacial",
        "agricultura, hortas e plantio",
        "medicina, hospital e atendimento",
        "justiça, leis e direitos",
        "emergências e imprevistos",
        "gerações e o passar do tempo",
        "arte, museus e exposições",
        "fotografia e imagem",
        "idiomas, sotaques e tradução",
        "profissões incomuns e ofícios antigos",
        "lendas, folclore e superstições",
        "moda, roupas e estilo",
        "entregas, encomendas e logística",
    };

    public static IReadOnlyList<string> Tenses { get; } = new[]
    {
        "passado",
        "presente",
        "futuro",
        "atemporal (fatos gerais, sem marcar tempo)",
    };

    /// <summary>
    /// Sem "diálogo" de propósito: o system prompt proíbe aspas, e um diálogo
    /// forçaria o modelo a escolher entre as duas regras.
    /// </summary>
    public static IReadOnlyList<string> Registers { get; } = new[]
    {
        "narrativo (conta algo que acontece)",
        "expositivo (explica um assunto)",
        "descritivo (descreve um lugar, objeto ou processo)",
        "instrucional (ensina a fazer algo)",
        "opinativo (defende um ponto de vista)",
        "comparativo (contrasta duas coisas)",
    };

    /// <summary>Letras que rendem nome natural em português; sem K/W/X/Y/Z/Q/U.</summary>
    public static IReadOnlyList<char> CharacterInitials { get; } = new[]
    {
        'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I',
        'J', 'L', 'M', 'N', 'O', 'P', 'R', 'S', 'T', 'V',
    };

    // Declarado ANTES de PointsOfView: inicializador estático roda em ordem textual,
    // e PointsOfView deriva desta lista.
    private static List<Perspective> Perspectives { get; } = new()
    {
        new("primeira pessoa (eu / nós)", NamedCharacter: false),
        new("terceira pessoa, com um personagem nomeado", NamedCharacter: true),
        new("sem personagem — o texto trata do assunto em si", NamedCharacter: false),
    };

    /// <summary>
    /// Pontos de vista. Só um dos três pede personagem nomeado — o que já corta a
    /// recorrência de nomes em dois terços, antes mesmo do sorteio da letra.
    /// </summary>
    public static IReadOnlyList<string> PointsOfView { get; } =
        Perspectives.ConvertAll(p => p.Label);

    /// <summary>
    /// Sorteia uma combinação. O <paramref name="random"/> é injetável só pra os
    /// testes fixarem a semente; em produção usa o <see cref="Random.Shared"/>.
    /// </summary>
    public static TextVariety Pick(Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var perspective = Perspectives[rng.Next(Perspectives.Count)];

        return new TextVariety(
            Domains[rng.Next(Domains.Count)],
            Tenses[rng.Next(Tenses.Count)],
            Registers[rng.Next(Registers.Count)],
            perspective.Label,
            perspective.NamedCharacter
                ? CharacterInitials[rng.Next(CharacterInitials.Count)]
                : null);
    }

    private sealed record Perspective(string Label, bool NamedCharacter);
}
