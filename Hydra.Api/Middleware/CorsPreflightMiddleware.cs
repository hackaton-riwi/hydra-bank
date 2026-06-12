using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Hydra.Api.Middleware;

public class CorsPreflightMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CorsPreflightMiddleware> _logger;

    public CorsPreflightMiddleware(RequestDelegate next, ILogger<CorsPreflightMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            _logger.LogDebug("Handled CORS preflight request");
            return;
        }

        await _next(context);
    }
}
