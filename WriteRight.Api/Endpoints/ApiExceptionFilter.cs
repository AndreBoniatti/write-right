using WriteRight.Api.Llm;
using WriteRight.Api.Services;

namespace WriteRight.Api.Endpoints;

/// <summary>
/// Traduz as exceções que o app lança de propósito em ProblemDetails. Filtro de
/// endpoint, e não middleware de exceção, pra o resto seguir o caminho de sempre: em
/// dev, a página de exceção (com o 400 de corpo malformado e a mensagem da key que
/// falta); em produção, o 500 do servidor.
/// </summary>
public sealed class ApiExceptionFilter : IEndpointFilter
{
    private readonly ILogger<ApiExceptionFilter> _log;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> log) => _log = log;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidRequestException ex)
        {
            return TypedResults.Problem(
                title: "Pedido inválido", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (LlmCallFailedException ex)
        {
            // A IA respondeu (e cobrou), mas a resposta não serviu: falha de quem está
            // atrás desta API, não dela. Foi paga, então vai pro log.
            _log.LogError(ex, "A IA não devolveu uma resposta utilizável.");
            return TypedResults.Problem(
                title: "A IA não devolveu uma resposta utilizável", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
