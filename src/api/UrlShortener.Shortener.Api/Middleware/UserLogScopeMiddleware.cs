namespace UrlShortener.Shortener.Api.Middleware;

/// <summary>
/// Adds the caller's user id to every log line written while handling an authenticated request, so a user's
/// activity can be found in the logs. Runs after authentication. Only the id is added, never the API key.
/// </summary>
internal sealed class UserLogScopeMiddleware(RequestDelegate next, ILogger<UserLogScopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.User.Identity?.IsAuthenticated == true ? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value : null;
        if (userId is null)
        {
            await next(context);
            return;
        }

        // A dictionary is the scope shape that the JSON console and OpenTelemetry turn into separate fields.
        using (logger.BeginScope(new Dictionary<string, object> { ["user_id"] = userId }))
        {
            await next(context);
        }
    }
}
