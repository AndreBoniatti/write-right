using System.Text.Json;
using WriteRight.Shared.Cards;

namespace WriteRight.Api.Llm;

/// <summary>
/// Prompt e schema do passo que desenha os cards de vocabulário, separado da correção.
///
/// <b>Por que um passo separado.</b> Antes, quem decidia o card era a CATEGORIA que a
/// correção dava ao erro — um rótulo feito pro perfil, não pro card. Medido no deck
/// real (2026-10-07, 24 práticas, 76 cards rotulados à mão antes de rodar): 32% dos
/// cards eram ruins — gramática arquivada como vocabulário, resposta alternativa
/// válida punida, trecho de frase no lugar de item. E o contrário também: "wait for"
/// e "receipt → recipe" nunca viraram card. Aqui o modelo responde a pergunta certa,
/// sobre o erro em si, e pode responder "não vira card".
///
/// <b>Este texto foi medido; mexa medindo.</b> Na mesma rodada:
/// <list type="bullet">
/// <item>Sem <c>alsoCorrect</c>, o modelo recusava todo item com sinônimo
/// ("advogado" → lawyer/attorney): 100% dos ruins barrados, mas só 70% dos bons
/// mantidos. Com a lista, 83% / 93%.</item>
/// <item>"preposição isolada" na lista de gramática custa "wait for"/"leave for";
/// sem ela, nasceu "in the cart". Ficou, porque card ruim é pior que card a menos.</item>
/// <item>Os portões que o código aplica depois (<c>CardGate</c>) fecham o resto.</item>
/// </list>
/// </summary>
internal static class CardPrompt
{
    public static string BuildSystemPrompt() => $"""
        Você cria flashcards de vocabulário a partir dos erros reais de um estudante que traduziu um texto.
        O card mostra uma frase do texto corrigido com uma lacuna no lugar de 'answer' e, como dica, 'hint' no idioma de origem. O estudante digita o que falta; o app aceita 'answer' ou qualquer resposta de 'alsoCorrect'.

        Um card errado custa caro: o estudante treina aquilo por meses e passa a desconfiar do deck inteiro. Deixar de criar um card custa pouco. Na dúvida, não crie.

        Para cada erro, decida 'verdict':
        - {nameof(CardDecisionKind.Card)}: o erro mostra uma palavra ou expressão que o estudante não sabia (palavra, colocação, expressão fixa, falso cognato), e a dica determina a resposta. Sinônimos que um nativo também usaria na lacuna não impedem o card: liste-os em 'alsoCorrect'.
        - {nameof(CardDecisionKind.Grammar)}: o problema é gramatical (tempo, concordância, artigo, pronome, preposição isolada, ordem, forma do comparativo...), mesmo que envolva palavras.
        - {nameof(CardDecisionKind.Acceptable)}: o que o estudante escreveu também é correto e natural; a correção é só preferência.
        - {nameof(CardDecisionKind.Ambiguous)}: a dica não determina a resposta, ou as respostas corretas são tantas que não cabem numa lista curta.
        - {nameof(CardDecisionKind.Other)}: ortografia, pontuação, ou nada a ensinar.

        Nos cards:
        - 'answer' = o menor trecho de 'correctedText' que contém o item, copiado literalmente.
        - 'hint' = o trecho do TEXTO ORIGINAL que 'answer' traduz. Exatamente esse trecho — nem mais, nem menos. Copiado literalmente.
        - 'alsoCorrect' = outras respostas que um nativo escreveria na lacuna e que também estão certas. Nunca inclua o que o estudante escreveu: se ele estava certo, o verdict é '{nameof(CardDecisionKind.Acceptable)}'.
        - Se dois erros ensinam o mesmo item, crie o card em um só.
        'item' = em poucas palavras, o que o card ensinaria (preencha sempre). Fora dos cards, 'answer', 'hint' e 'alsoCorrect' ficam vazios.
        """;

    /// <summary>
    /// Os erros vão numerados pela posição — é o número que o modelo devolve em
    /// 'error'. Quem monta a lista e quem lê a resposta precisam usar a MESMA ordem.
    /// </summary>
    public static string BuildUserMessage(CardDesignRequest request)
    {
        var errors = request.Errors.Select((e, i) => $"{i + 1}. \"{e.Original}\" → \"{e.Correction}\"");

        return $"Idioma de origem: {LlmText.LanguageName(request.SourceLanguage)}\n" +
               $"Idioma-alvo: {LlmText.LanguageName(request.TargetLanguage)}\n\n" +
               $"TEXTO ORIGINAL:\n{request.SourceText}\n\n" +
               $"TRADUÇÃO DO ESTUDANTE:\n{request.UserTranslation}\n\n" +
               $"correctedText:\n{request.CorrectedText}\n\n" +
               "ERROS:\n" + string.Join("\n", errors);
    }

    /// <summary>
    /// Schema do <see cref="CardDesign"/>. A ordem dos campos foi a medida: 'verdict'
    /// antes de 'answer' faz o modelo decidir SE é card antes de recortar o card.
    /// </summary>
    public static Dictionary<string, JsonElement> BuildResultSchema()
    {
        var decision = new
        {
            type = "object",
            properties = new
            {
                error = new { type = "integer" },
                item = new { type = "string" },
                verdict = new { type = "string", @enum = Enum.GetNames<CardDecisionKind>() },
                answer = new { type = "string" },
                hint = new { type = "string" },
                alsoCorrect = new { type = "array", items = new { type = "string" } },
            },
            required = new[] { "error", "item", "verdict", "answer", "hint", "alsoCorrect" },
            additionalProperties = false,
        };

        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                decisions = new { type = "array", items = decision },
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "decisions" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
    }
}
