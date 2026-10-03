using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TeamBuilder.Api.Errors;

namespace TeamBuilder.Tests.Integration;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ArgumentException_ReturnsBadRequestWithExceptionMessage()
    {
        var (statusCode, contentType, responseBody) =
            await HandleAsync(new ArgumentException("The request value is invalid."));

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        contentType.Should().StartWith("application/json");
        using var problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status400BadRequest);
        problem.RootElement.GetProperty("title").GetString().Should().Be("Bad Request");
        problem.RootElement.GetProperty("detail").GetString().Should().Be("The request value is invalid.");
    }

    [Fact]
    public async Task TryHandleAsync_InvalidOperationException_ReturnsConflictWithExceptionMessage()
    {
        var (statusCode, contentType, responseBody) =
            await HandleAsync(new InvalidOperationException("The operation conflicts with current state."));

        statusCode.Should().Be(StatusCodes.Status409Conflict);
        contentType.Should().StartWith("application/json");
        using var problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status409Conflict);
        problem.RootElement.GetProperty("title").GetString().Should().Be("Conflict");
        problem.RootElement.GetProperty("detail").GetString()
            .Should().Be("The operation conflicts with current state.");
    }

    [Fact]
    public async Task TryHandleAsync_UnexpectedException_ReturnsGenericProblemDetailsWithoutInternalMessage()
    {
        const string internalMessage = "Database server db-internal-01 failed with sensitive detail.";

        var (statusCode, contentType, responseBody) =
            await HandleAsync(new Exception(internalMessage));

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        contentType.Should().StartWith("application/json");
        responseBody.Should().NotContain(internalMessage);
        using var problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status500InternalServerError);
        problem.RootElement.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        problem.RootElement.GetProperty("detail").GetString().Should().Be("An unexpected error occurred.");
    }

    private static async Task<(int StatusCode, string? ContentType, string Body)> HandleAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        (await handler.TryHandleAsync(context, exception, CancellationToken.None)).Should().BeTrue();

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, context.Response.ContentType, body);
    }
}
