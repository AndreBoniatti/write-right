using System.Collections.Generic;

namespace WriteRight.Shared.Cards;

/// <summary>
/// Um erro da prática do jeito que o desenhista de cards o recebe: só o par
/// escrito → corrigido. Categoria e explicação ficam de fora de propósito — a
/// categoria é justamente o rótulo que errava ao decidir o que vira card, e quem
/// decide agora é o modelo, olhando o erro em si.
///
/// O erro é identificado pela POSIÇÃO na lista (1, 2, 3…), que é como o modelo o cita
/// de volta em <see cref="CardProposal.Error"/>.
/// </summary>
public sealed record CardDesignError(string Original, string Correction);

/// <summary>O que o modelo recebe pra desenhar os cards de uma prática já corrigida.</summary>
public sealed record CardDesignRequest(
    Language SourceLanguage,
    Language TargetLanguage,
    string SourceText,
    string UserTranslation,
    string CorrectedText,
    IReadOnlyList<CardDesignError> Errors);

/// <summary>O veredito do modelo sobre um erro.</summary>
public enum CardDecisionKind
{
    /// <summary>Item léxico que o aluno não sabia, e a dica determina a resposta.</summary>
    Card,

    /// <summary>Problema gramatical — quem cuida é o loop de categorias, não o deck.</summary>
    Grammar,

    /// <summary>O que o aluno escreveu também estava certo; a correção foi preferência.</summary>
    Acceptable,

    /// <summary>A dica não determina a resposta, ou as respostas certas não cabem numa lista curta.</summary>
    Ambiguous,

    /// <summary>Ortografia, pontuação, ou nada a ensinar.</summary>
    Other,
}

/// <summary>
/// A decisão do modelo sobre UM erro, crua — ainda não passou pelo portão do código
/// (<c>CardGate</c>). Um <see cref="CardDecisionKind.Card"/> aqui é proposta, não card.
/// </summary>
/// <param name="Error">Posição do erro na lista enviada, começando em 1.</param>
/// <param name="Item">
/// O que o card ensinaria, em poucas palavras. Nenhum código lê este campo, e isso é
/// de propósito: ele existe pro MODELO, que nomeia o item antes de decidir e recortar —
/// faz parte do prompt que foi medido. Tirá-lo do schema muda o comportamento medido.
/// </param>
/// <param name="Answer">Trecho literal do texto corrigido que vira a lacuna.</param>
/// <param name="Hint">Trecho literal do texto de origem que a resposta traduz.</param>
/// <param name="AlsoCorrect">Outras respostas certas para a lacuna, que a conferência aceita.</param>
public sealed record CardProposal(
    int Error,
    string Item,
    CardDecisionKind Verdict,
    string Answer,
    string Hint,
    IReadOnlyList<string> AlsoCorrect);

/// <summary>Saída do modelo: uma decisão por erro.</summary>
public sealed record CardDesign(IReadOnlyList<CardProposal> Decisions);
