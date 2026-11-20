using GatewayService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GatewayService.Resilience;

/// <summary>Критичный сервис недоступен → 503 { "message": "X Service unavailable" }.</summary>
public sealed class ServiceUnavailableFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ServiceUnavailableException e)
            return;

        context.Result = new ObjectResult(new ErrorResponse(e.Message))
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
        context.ExceptionHandled = true;
    }
}
