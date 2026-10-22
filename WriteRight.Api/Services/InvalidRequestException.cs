namespace WriteRight.Api.Services;

/// <summary>
/// Pedido que o serviço recusa antes de qualquer trabalho (idiomas iguais, tradução em
/// branco). Na borda HTTP vira 400 — ver <c>ApiExceptionFilter</c>.
/// </summary>
public sealed class InvalidRequestException : Exception
{
    public InvalidRequestException(string message) : base(message) { }
}
