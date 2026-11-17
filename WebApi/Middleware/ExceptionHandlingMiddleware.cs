using Microsoft.AspNetCore.Mvc;

namespace WebApi.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unhandled exception while processing {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal server error"
            };

            if (!await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = problem
            }))
            {
                // решение: если ни один writer не поддерживает Accept, сохраняем формат ProblemDetails в JSON.
                await context.Response.WriteAsJsonAsync(problem, options: null,
                    contentType: "application/problem+json", cancellationToken: context.RequestAborted);
            }
        }
    }
}
