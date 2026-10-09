using System.Net;
using System.Text;

namespace Receivify.Tests;

/// <summary>One request as the SDK actually built it.</summary>
internal sealed class RecordedRequest
{
    public required HttpMethod Method { get; init; }

    public required Uri Uri { get; init; }

    public required string? Body { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public string? Header(string name) =>
        Headers.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>
/// The injected HTTP layer. Every test drives the SDK through this — no socket is ever opened, so
/// the suite is entirely offline.
/// </summary>
internal sealed class FakeTransport : HttpMessageHandler
{
    private readonly Queue<QueuedResponse> _responses = new();

    public List<RecordedRequest> Requests { get; } = new();

    public RecordedRequest Last => Requests[^1];

    public FakeTransport Enqueue(int status, string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        _responses.Enqueue(new QueuedResponse(status, body, headers));
        return this;
    }

    public FakeTransport EnqueueOk(string body) => Enqueue(200, body);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        string? body = null;
        if (request.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        Requests.Add(new RecordedRequest
        {
            Method = request.Method,
            Uri = request.RequestUri!,
            Body = body,
            Headers = headers,
        });

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                "FakeTransport ran out of queued responses at request "
                + Requests.Count + " (" + request.Method + " " + request.RequestUri + ").");
        }

        QueuedResponse queued = _responses.Dequeue();

        var response = new HttpResponseMessage((HttpStatusCode)queued.Status)
        {
            Content = new StringContent(queued.Body, Encoding.UTF8, "application/json"),
        };

        if (queued.Headers is not null)
        {
            foreach (KeyValuePair<string, string> header in queued.Headers)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return response;
    }

    private sealed record QueuedResponse(int Status, string Body, IReadOnlyDictionary<string, string>? Headers);
}

/// <summary>Wires a <see cref="FakeTransport"/> to a client the way a real integration would.</summary>
internal static class TestClient
{
    public const string ApiUrl = "https://receivify.test";

    public const string ApiKey = "rcv_live_thiskeymustneverbelogged";

    public static (ReceivifyClient Client, FakeTransport Transport) Create(
        Action<ReceivifyClientOptions>? configure = null)
    {
        var transport = new FakeTransport();
        var options = new ReceivifyClientOptions
        {
            // Retries must not actually sleep in a unit test.
            DelayAsync = (_, _) => Task.CompletedTask,
        };

        configure?.Invoke(options);

        var client = new ReceivifyClient(new HttpClient(transport), ApiUrl, ApiKey, options);
        return (client, transport);
    }
}
