using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace VK4DotNet.Tests;

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body, string? ContentType, string? UserAgent);

internal sealed class QueueHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        _responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            body,
            request.Content?.Headers.ContentType?.ToString(),
            request.Headers.UserAgent.ToString()));

        if (!_responses.TryDequeue(out var response))
        {
            throw new InvalidOperationException($"No queued response for {request.Method} {request.RequestUri}.");
        }

        return await response(request, cancellationToken);
    }
}
