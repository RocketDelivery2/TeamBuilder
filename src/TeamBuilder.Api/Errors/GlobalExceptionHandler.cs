using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Application.Exceptions;

namespace TeamBuilder.Api.Errors;

/// <summary>
/// Maps unhandled application exceptions to consistent ProblemDetails responses.
/// ArgumentException => 400, InvalidOperationException => 409, everything else => 500.
/// A <see cref="RosterConflictException"/> or <see cref="RosterValidationException"/> also
/// carries its stable <c>code</c>.
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title) = exception switch
        {
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Conflict"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);
        else
            logger.LogWarning(exception, "Handled exception ({StatusCode}): {Message}", statusCode, exception.Message);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = statusCode == StatusCodes.Status500InternalServerError
                ? "An unexpected error occurred."
                : exception.Message
        };

        if (exception is RosterConflictException rosterConflict)
            problemDetails.Extensions["code"] = rosterConflict.Code;
        else if (exception is RosterValidationException rosterValidation)
            problemDetails.Extensions["code"] = rosterValidation.Code;

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
