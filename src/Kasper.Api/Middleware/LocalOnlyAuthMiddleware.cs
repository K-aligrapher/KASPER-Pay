using Kasper.Application.Contracts;
using Kasper.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Kasper.Api.Middleware;

/// <summary>
/// NFR-001: every /api call needs the per-launch bearer token, and the Host header must be
/// loopback (blocks DNS-rebinding from web pages). Static UI files and /health are public
/// because they contain no secrets or private data.
/// </summary>
internal sealed class LocalOnlyAuthMiddleware(RequestDelegate next, string token)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (host is not ("127.0.0.1" or "localhost"))
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/ready"))
        {
            var header = context.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : null;
            if (!TokenGenerator.FixedTimeEquals(supplied, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorDto("unauthorized", "Missing or invalid session token."));
                return;
            }
        }

        await next(context);
    }
}
