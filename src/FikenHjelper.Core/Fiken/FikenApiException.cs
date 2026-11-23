using System.Net;

namespace FikenHjelper.Core.Fiken;

public sealed class FikenApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string UserMessage { get; }
    public string? Detail { get; }

    public FikenApiException(
        HttpStatusCode statusCode,
        string userMessage,
        string? detail = null,
        Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(detail) ? userMessage : $"{userMessage} ({detail})", innerException)
    {
        StatusCode = statusCode;
        UserMessage = userMessage;
        Detail = detail;
    }
}
