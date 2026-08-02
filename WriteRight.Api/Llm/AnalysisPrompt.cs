using System.Text;
using System.Text.Json;
using WriteRight.Shared.Analysis;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Llm;

/// <summary>
/// Constrói o prompt da análise de fraquezas e o schema do structured output.
///
/// A tarefa aqui é diferente da correção: a correção olha UM texto e classifica;
/// esta olha o histórico de erros e procura a ESTRUTURA por trás deles — a sub-regra
/// dentro da categoria e o fio que liga categorias diferentes. É o que o agregado
/// por categoria, sozinho, não consegue dizer.
/// </summary>
internal static class AnalysisPrompt
{
    /// <summary>
    /// System prompt. Duas travas moldam quase tudo aqui: (1) nenhuma afirmação sem
    /// evidência citada por id, e (2) menos padrões bem lastreados vale mais que a
    /// lista cheia — sem isso o modelo preenche o teto com padrão esticado, e no card
    /// o esticado fica indistinguível do real.
    /// </summary>
    public static string BuildSystemPrompt(AnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um professor de idiomas analisando o histórico de erros de um aluno.");
        sb.AppendLine("Recebe os erros reais que ele cometeu (cada um com um id) e o agregado por categoria.");
        sb.AppendLine("Sua tarefa: DIAGNOSTICAR o estado atual dele — não elogiar, não motivar, não dar conselho genérico.");
        sb.AppendLine();
        sb.AppendLine("O QUE É UM BOM PADRÃO:");
        sb.AppendLine("- A SUB-REGRA dentro de uma categoria. \"Preposição\" não é padrão: é a categoria.");
        sb.AppendLine("  \"Usa 'in' onde o inglês pede 'on' em expressão de tempo\" é padrão — é uma regra só, acionável.");
        sb.AppendLine("- O FIO que liga categorias diferentes. Se tradução literal, ordem das palavras e");
        sb.AppendLine("  naturalidade aparecem juntas, provavelmente é UM problema (traduzir palavra a palavra");
        sb.AppendLine("  em vez de reestruturar a frase), não três.");
        sb.AppendLine("- A CAUSA no idioma nativo, quando a evidência mostrar. As explicações estão em português:");
        sb.AppendLine("  use isso pra nomear a interferência do idioma do aluno quando ela for a origem real do erro.");
        sb.AppendLine();
        sb.AppendLine("REGRAS DE EVIDÊNCIA (as mais importantes):");
        sb.AppendLine($"- Todo padrão precisa citar pelo menos {request.MinEvidence} ids de erros REAIS da lista recebida.");
        sb.AppendLine("- Use SOMENTE ids que aparecem na lista. Id inventado invalida o padrão inteiro.");
        sb.AppendLine($"- Isto é conferido: um padrão com menos de {request.MinEvidence} ids válidos é DESCARTADO");
        sb.AppendLine("  antes de chegar ao aluno. Ids repetidos contam uma vez só.");
        sb.AppendLine("- Se algo parece um padrão mas você só acha 1 ou 2 casos, NÃO reporte. É ruído.");
        sb.AppendLine($"- No máximo {request.MaxPatterns} padrões, do mais importante pro menos.");
        sb.AppendLine("- MENOS padrões bem evidenciados é uma resposta MELHOR que a lista cheia de padrões");
        sb.AppendLine("  esticados. Se os dados só sustentam 1, devolva 1. Não preencha o teto.");
        sb.AppendLine();
        sb.AppendLine("ITENS DE ESTUDO:");
        sb.AppendLine("- 'Rule': regra FECHADA, que cabe num parágrafo. Ensine ela ali mesmo, com exemplos");
        sb.AppendLine("  curtos de certo/errado. Ex.: in/on/at de tempo, artigo com incontáveis, -ed vs -ing.");
        sb.AppendLine("- 'Topic': habilidade AMPLA que parágrafo nenhum ensina (naturalidade, repertório de");
        sb.AppendLine("  colocações). Diga o que buscar e por quê; não finja ensinar.");
        sb.AppendLine("- Cada item deve nascer de um padrão que você reportou. Nada solto.");
        sb.AppendLine($"- No máximo {request.MaxStudyItems} itens.");
        sb.AppendLine();
        sb.AppendLine("PROIBIDO:");
        sb.AppendLine("- Estimar nível (CEFR ou qualquer outro). Os dados não sustentam isso.");
        sb.AppendLine("- Citar sites, livros, cursos ou links. Você não tem como verificar que existem.");
        sb.AppendLine("- Comentar o que a lista de erros não mostra (tamanho de frase, repetição de vocabulário,");
        sb.AppendLine("  variedade de estrutura). Você recebe só as linhas de erro, não os textos: falar disso");
        sb.AppendLine("  seria afirmação sem lastro.");
        sb.AppendLine("- Elogio de abertura, tom motivacional, clichê (\"ótimo progresso\", \"continue assim\").");
        sb.AppendLine("- Chamar o aluno por um nome.");
        sb.AppendLine();
        sb.AppendLine("FORMA:");
        sb.AppendLine("- Escreva TUDO em português do Brasil (os exemplos de inglês, claro, em inglês).");
        sb.AppendLine("- 'title': o padrão em uma linha curta, específica.");
        sb.AppendLine("- 'diagnosis': 2 a 4 frases — o que é, por que acontece, e o que muda na prática.");
        sb.AppendLine("  Fale direto na 2ª pessoa (\"você usa...\", \"seu texto...\").");
        sb.AppendLine();
        sb.AppendLine("CATEGORIAS (o vocabulário do agregado que você recebe):");
        foreach (var info in ErrorCatalog.All)
            sb.Append("- ").Append(info.Category).Append(": ").Append(info.Description).AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Mensagem do usuário: o agregado vitalício (mapa do todo, barato) e os erros
    /// reais da janela, do mais recente pro mais antigo — cada um com o id que a
    /// evidência vai citar.
    /// </summary>
    public static string BuildUserMessage(AnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("Práticas concluídas no histórico todo: ").Append(request.LifetimePractices).AppendLine();
        sb.Append("Janela analisada: as ").Append(request.PracticesAnalyzed)
          .AppendLine(" práticas concluídas mais recentes.");
        sb.AppendLine();

        sb.AppendLine("AGREGADO DO HISTÓRICO (categoria | erros | peso por gravidade):");
        if (request.LifetimeByCategory.Count == 0)
        {
            sb.AppendLine("(vazio)");
        }
        else
        {
            foreach (var c in request.LifetimeByCategory)
                sb.Append("- ").Append(c.Category).Append(" | ").Append(c.Count)
                  .Append(" | ").Append(c.Score).AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("ERROS REAIS DA JANELA (id | categoria | gravidade | escreveu → correto | porquê):");
        foreach (var e in request.Errors)
        {
            sb.Append(e.Id).Append(" | ").Append(e.Category).Append(" | ").Append(e.Severity)
              .Append(" | ").Append(e.Original).Append(" → ").Append(e.Correction)
              .Append(" | ").Append(e.Explanation).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// JSON schema do <see cref="AnalysisDraft"/>.
    ///
    /// <b>Sem <c>minItems</c>/<c>maxItems</c> de propósito</b>: o structured output da
    /// Anthropic não aceita restrição de cardinalidade em array (devolve 400
    /// "property 'maxItems' is not supported"). Então o teto de padrões e o piso de
    /// evidência vivem em DOIS lugares, nenhum deles no schema: o prompt pede, e o
    /// servidor confere no <c>AnalysisService</c>. A conferência é o que vale — o
    /// schema nunca soube se um id era real, então a trava sempre foi do servidor.
    /// </summary>
    public static Dictionary<string, JsonElement> BuildResultSchema(AnalysisRequest request)
    {
        var patternItem = new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string" },
                diagnosis = new { type = "string" },
                evidenceErrorIds = new
                {
                    type = "array",
                    items = new { type = "integer" },
                },
            },
            required = new[] { "title", "diagnosis", "evidenceErrorIds" },
            additionalProperties = false,
        };

        var studyItem = new
        {
            type = "object",
            properties = new
            {
                kind = new { type = "string", @enum = Enum.GetNames<StudyItemKind>() },
                title = new { type = "string" },
                content = new { type = "string" },
            },
            required = new[] { "kind", "title", "content" },
            additionalProperties = false,
        };

        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                patterns = new { type = "array", items = patternItem },
                studyItems = new { type = "array", items = studyItem },
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "patterns", "studyItems" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
    }
}
