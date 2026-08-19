namespace WriteRight.Shared.Cards;

/// <summary>
/// A lacuna do card. Vive no <c>Shared</c> porque o servidor a ESCREVE (ao recortar
/// a frase) e o cliente a LÊ (pra desenhar o espaço a preencher) — marcador em dois
/// lugares seria a receita pra um lado mudar e o outro parar de reconhecer, sem
/// nada quebrar de forma visível.
/// </summary>
public static class Cloze
{
    /// <summary>Marcador que ocupa o lugar da resposta dentro do enunciado.</summary>
    public const string Blank = "___";

    /// <summary>
    /// Parte o enunciado no que vem antes e depois da lacuna. Null quando não há
    /// marcador — enunciado que não é cloze, e o chamador decide o que fazer.
    /// Só a PRIMEIRA ocorrência conta: é a que o card manda responder.
    /// </summary>
    public static (string Before, string After)? Split(string prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return null;

        var at = prompt.IndexOf(Blank, StringComparison.Ordinal);
        if (at < 0) return null;

        return (prompt[..at], prompt[(at + Blank.Length)..]);
    }
}
