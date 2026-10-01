using System.Text.Json;

namespace Mair.OperationsService.Api;

/// <summary>
/// RFC 9457 problem details, the only error body the API returns (<c>OPERATIONS_API.md</c> §10a).
/// Written by hand rather than through the framework's <c>ProblemDetails</c> service so the bytes are
/// exactly the contract's <c>Problem</c> schema, whichever part of the pipeline refused the request.
/// </summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    public static Task WriteAsync(HttpContext context, int status, string title, string? detail = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("type"u8, "about:blank"u8);
            w.WriteString("title"u8, title);
            w.WriteNumber("status"u8, status);
            if (detail is not null)
            {
                w.WriteString("detail"u8, detail);
            }

            w.WriteString("instance"u8, context.Request.Path.Value);
            w.WriteEndObject();
        }

        return context.Response.Body.WriteAsync(stream.ToArray()).AsTask();
    }

    /// <summary>A refusal from a handler: the result writes the problem when executed.</summary>
    public static IResult Result(int status, string title, string? detail = null) => new ProblemResult(status, title, detail);

    private sealed record ProblemResult(int Status, string Title, string? Detail) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext, Status, Title, Detail);
    }
}

/// <summary>A request parameter the contract does not accept: answered <c>400</c>.</summary>
public sealed class BadRequestException(string detail) : Exception(detail);
