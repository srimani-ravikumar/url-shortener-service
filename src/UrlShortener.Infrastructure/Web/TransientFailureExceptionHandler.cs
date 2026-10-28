using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace UrlShortener.Infrastructure.Web;

/// <summary>
/// Turns "Redis/Cassandra is temporarily unavailable" into a 503 with Retry-After, so callers know to retry.
/// Every other exception is left to the default handler, which answers 500 without leaking details.
/// Shared by every HTTP service; register it with <c>AddTransientFailureHandling</c>.
/// </summary>
public sealed class TransientFailureExceptionHandler(
    ILogger<TransientFailureExceptionHandler> logger,
    IProblemDetailsService problemDetails) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!TransientFailure.IsTransient(exception))
        {
            return false;
        }

        // Returning true stops the framework's own logging for this exception, so log it here.
        logger.LogWarning(exception, "Storage unavailable while handling {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "5";

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service temporarily unavailable",
                Detail = "A required service is not responding. Please retry shortly.",
            },
        });
    }
}
