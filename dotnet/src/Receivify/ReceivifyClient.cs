using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Receivify;

/// <summary>
/// Options for <see cref="ReceivifyClient"/>.
/// </summary>
public sealed class ReceivifyClientOptions
{
    /// <summary>Per-request timeout. Defaults to 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The webhook signing secrets, <b>current first, previous second</b>.
    /// </summary>
    /// <remarks>
    /// This is a list and not a single string because a rotation keeps the previous secret
    /// acceptable for a grace window, so a receiver must legitimately try both. See
    /// <see cref="ReceivifyWebhooks"/>.
    /// </remarks>
    public IReadOnlyList<string> WebhookSecrets { get; set; } = Array.Empty<string>();

    /// <summary>Convenience for the single-secret case; reads and writes <see cref="WebhookSecrets"/>.</summary>
    public string? WebhookSecret
    {
        get => WebhookSecrets.Count > 0 ? WebhookSecrets[0] : null;
        set => WebhookSecrets = value is null ? Array.Empty<string>() : new[] { value };
    }

    /// <summary>How many times to retry a SAFE request. Defaults to 2 (so up to 3 attempts).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>The first backoff step; it doubles each attempt. Defaults to 500ms.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The ceiling on any single backoff wait. Defaults to 20 seconds.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Appended to the <c>receivify-dotnet/&lt;version&gt;</c> User-Agent.</summary>
    public string? UserAgentSuffix { get; set; }

    /// <summary>Inject a clock. Tests only.</summary>
    public Func<DateTimeOffset>? UtcNow { get; set; }

    /// <summary>Inject the backoff wait. Tests only — pass a no-op to make retries instant.</summary>
    public Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; set; }
}

/// <summary>
/// The Receivify API client.
/// </summary>
/// <remarks>
/// <para>
/// Zero third-party dependencies: <see cref="HttpClient"/>, <c>System.Text.Json</c> and
/// <c>System.Security.Cryptography</c>, nothing else.
/// </para>
/// <para>
/// <b>Give it an <see cref="HttpClient"/> you control.</b> The
/// <see cref="ReceivifyClient(HttpClient, string, string, ReceivifyClientOptions)"/> constructor is
/// the one to use from DI, and it is also the seam tests inject a fake transport through. The
/// convenience constructor creates and owns exactly one <see cref="HttpClient"/> for the lifetime
/// of the client — a client is meant to be long-lived and shared. Never construct one per request:
/// that is how you exhaust sockets.
/// </para>
/// <para>
/// <b>Retry policy.</b> A request is retried on 429 and 5xx only when it is safe: a <c>GET</c>, or
/// a mutating call that both carries a <see cref="RequestOptions.IdempotencyKey"/> AND targets an
/// endpoint that declares <c>Idempotency-Key</c> support in the spec. A key supplied to an endpoint
/// that does not support it is still sent but does not unlock retries, because the server would not
/// deduplicate it. Blind-retrying a mutating call is how you double-charge someone. Backoff honours
/// <c>Retry-After</c> first, then <c>X-RateLimit-Reset</c>.
/// </para>
/// <para>
/// The API key is held in a private field. It is never placed in a URL, never logged, and never
/// appears on a thrown exception.
/// </para>
/// </remarks>
public sealed partial class ReceivifyClient : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string? _apiKey;
    private readonly ReceivifyClientOptions _options;
    private readonly string _userAgent;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    private bool _disposed;

    /// <summary>
    /// Creates a client that owns a single internal <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="apiUrl">The deployment base URL, e.g. <c>https://receivify.io</c>.</param>
    /// <param name="apiKey">The merchant <c>rcv_live_…</c> key. Optional if you only call public routes.</param>
    /// <param name="options">Timeouts, retries and webhook secrets.</param>
    public ReceivifyClient(string apiUrl, string? apiKey = null, ReceivifyClientOptions? options = null)
        : this(CreateOwnedHttpClient(), apiUrl, apiKey, options, ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Creates a client over an <see cref="HttpClient"/> you own — the constructor to use from DI,
    /// and the seam a test injects a fake <see cref="HttpMessageHandler"/> through.
    /// </summary>
    /// <remarks>
    /// The supplied client is neither reconfigured nor disposed. Note that its own
    /// <see cref="HttpClient.Timeout"/> still applies alongside this SDK's per-request timeout,
    /// whichever fires first; <see cref="HttpClient.BaseAddress"/> is ignored because every request
    /// is built as an absolute URI from <paramref name="apiUrl"/>.
    /// </remarks>
    /// <param name="httpClient">A shared, long-lived client.</param>
    /// <param name="apiUrl">The deployment base URL, e.g. <c>https://receivify.io</c>.</param>
    /// <param name="apiKey">The merchant <c>rcv_live_…</c> key. Optional if you only call public routes.</param>
    /// <param name="options">Timeouts, retries and webhook secrets.</param>
    public ReceivifyClient(
        HttpClient httpClient,
        string apiUrl,
        string? apiKey = null,
        ReceivifyClientOptions? options = null)
        : this(httpClient, apiUrl, apiKey, options, ownsHttpClient: false)
    {
    }

    private ReceivifyClient(
        HttpClient httpClient,
        string apiUrl,
        string? apiKey,
        ReceivifyClientOptions? options,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);

        _http = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _apiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
        _options = options ?? new ReceivifyClientOptions();
        _delayAsync = _options.DelayAsync ?? ((delay, token) => Task.Delay(delay, token));

        ApiUrl = apiUrl.Trim().TrimEnd('/');

        _userAgent = string.IsNullOrWhiteSpace(_options.UserAgentSuffix)
            ? SdkVersion.UserAgent
            : SdkVersion.UserAgent + " " + _options.UserAgentSuffix.Trim();

        WebhookSecrets = ReceivifyWebhooks.NormaliseSecrets(_options.WebhookSecrets);
    }

    /// <summary>The deployment base URL, with any trailing slash removed.</summary>
    public string ApiUrl { get; }

    /// <summary>The SDK version, also sent as the User-Agent.</summary>
    public static string Version => SdkVersion.Value;

    /// <summary>
    /// The configured webhook secrets, current first. Always a list, even when one secret was
    /// supplied.
    /// </summary>
    public IReadOnlyList<string> WebhookSecrets { get; }

    /// <summary>Releases the internal <see cref="HttpClient"/>, if this client created one.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    /* ================================================================ *
     * Webhooks — receiving
     * ================================================================ */

    /// <summary>
    /// Verify a signed delivery against the client's configured secrets.
    /// </summary>
    /// <remarks>
    /// <b>Pass the RAW body bytes.</b> Re-serialising a parsed object changes key order and
    /// whitespace, and the HMAC will not match a genuine delivery. In ASP.NET Core read
    /// <c>HttpRequest.Body</c> before any model binding.
    /// </remarks>
    /// <param name="rawBody">The EXACT bytes of the request body.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">Overrides the client's configured secrets when supplied.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public bool VerifyWebhook(
        byte[] rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets = null,
        WebhookVerifyOptions? options = null) =>
        ReceivifyWebhooks.Verify(rawBody, signatureHeader, secrets ?? WebhookSecrets, options);

    /// <summary>Verify a delivery whose body you already hold as a string. See the byte overload.</summary>
    /// <param name="rawBody">The EXACT body text.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">Overrides the client's configured secrets when supplied.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public bool VerifyWebhook(
        string rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets = null,
        WebhookVerifyOptions? options = null) =>
        ReceivifyWebhooks.Verify(rawBody, signatureHeader, secrets ?? WebhookSecrets, options);

    /// <summary>Verify and deserialise in one step. Throws <see cref="ReceivifyWebhookException"/> on failure.</summary>
    /// <param name="rawBody">The EXACT bytes of the request body.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">Overrides the client's configured secrets when supplied.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public WebhookEvent ParseWebhook(
        byte[] rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets = null,
        WebhookVerifyOptions? options = null) =>
        ReceivifyWebhooks.Parse(rawBody, signatureHeader, secrets ?? WebhookSecrets, options);

    /// <summary>Verify and deserialise a string body in one step.</summary>
    /// <param name="rawBody">The EXACT body text.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">Overrides the client's configured secrets when supplied.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public WebhookEvent ParseWebhook(
        string rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets = null,
        WebhookVerifyOptions? options = null) =>
        ReceivifyWebhooks.Parse(rawBody, signatureHeader, secrets ?? WebhookSecrets, options);

    /// <summary>The same check as <c>VerifyWebhook</c>, with a reason worth logging.</summary>
    /// <param name="rawBody">The EXACT bytes of the request body.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">Overrides the client's configured secrets when supplied.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public WebhookVerificationResult DescribeWebhook(
        byte[] rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets = null,
        WebhookVerifyOptions? options = null) =>
        ReceivifyWebhooks.Describe(rawBody, signatureHeader, secrets ?? WebhookSecrets, options);

    /* ================================================================ *
     * HTTP
     * ================================================================ */

    /// <summary>
    /// Issue a request and return the decoded body, throwing a typed exception on 4xx/5xx.
    /// </summary>
    /// <remarks>Public so you can reach an endpoint this SDK version does not model yet.</remarks>
    /// <typeparam name="T">The expected response type.</typeparam>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">An absolute path starting with <c>/</c>.</param>
    /// <param name="query">Query parameters, already un-encoded.</param>
    /// <param name="body">An object to serialise as the JSON body, or null.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="requiresAuth">Whether to send the bearer key.</param>
    /// <param name="supportsIdempotencyKey">Whether the endpoint declares <c>Idempotency-Key</c> support.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<T?> RequestAsync<T>(
        HttpMethod method,
        string path,
        IReadOnlyList<KeyValuePair<string, string>>? query = null,
        object? body = null,
        RequestOptions? options = null,
        bool requiresAuth = true,
        bool supportsIdempotencyKey = false,
        CancellationToken cancellationToken = default)
    {
        ReceivifyResponse<T> response = await SendAsync<T>(
            method,
            path,
            query,
            body,
            options,
            requiresAuth,
            supportsIdempotencyKey,
            throwOnError: true,
            cancellationToken).ConfigureAwait(false);

        return response.Value;
    }

    /// <summary>
    /// The same request, but returning status, headers and the raw body alongside the decoded value.
    /// </summary>
    /// <typeparam name="T">The expected response type.</typeparam>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">An absolute path starting with <c>/</c>.</param>
    /// <param name="query">Query parameters, already un-encoded.</param>
    /// <param name="body">An object to serialise as the JSON body, or null.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="requiresAuth">Whether to send the bearer key.</param>
    /// <param name="supportsIdempotencyKey">Whether the endpoint declares <c>Idempotency-Key</c> support.</param>
    /// <param name="throwOnError">
    /// When false, a 4xx/5xx is returned rather than thrown. Used for <c>GET /api/health</c>, whose
    /// 503 body is deliberately NOT an <c>{ error, detail }</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<ReceivifyResponse<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        IReadOnlyList<KeyValuePair<string, string>>? query = null,
        object? body = null,
        RequestOptions? options = null,
        bool requiresAuth = true,
        bool supportsIdempotencyKey = false,
        bool throwOnError = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string url = ApiUrl + path + BuildQueryString(query);
        string label = method.Method + " " + path;
        string? idempotencyKey = options?.IdempotencyKey;
        TimeSpan timeout = options?.Timeout ?? _options.Timeout;

        string? payload = body is null
            ? null
            : JsonSerializer.Serialize(body, body.GetType(), ReceivifyJson.Options);

        // A GET is safe by definition. A mutating call is safe ONLY when the endpoint itself
        // declares Idempotency-Key support AND the caller supplied one.
        bool safeToRetry = method == HttpMethod.Get
            || (supportsIdempotencyKey && !string.IsNullOrEmpty(idempotencyKey));
        int maxAttempts = safeToRetry ? Math.Max(0, _options.MaxRetries) + 1 : 1;

        for (int attempt = 1; ; attempt++)
        {
            int statusCode;
            string? rawBody;
            IReadOnlyDictionary<string, string> headers;

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            try
            {
                using HttpRequestMessage request = BuildRequest(
                    method,
                    url,
                    payload,
                    requiresAuth,
                    idempotencyKey,
                    options);

                using HttpResponseMessage response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                    .ConfigureAwait(false);

                statusCode = (int)response.StatusCode;
                headers = CollectHeaders(response);
                rawBody = statusCode == 204
                    ? null
                    : await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var timeoutError = new ReceivifyConnectionException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Receivify {label} timed out after {timeout.TotalMilliseconds:0}ms."),
                    isTimeout: true,
                    ex);

                if (!safeToRetry || attempt >= maxAttempts)
                {
                    throw timeoutError;
                }

                await _delayAsync(ComputeBackoff(attempt, null), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                var connectionError = new ReceivifyConnectionException(
                    "Receivify " + label + " failed: " + ex.Message,
                    isTimeout: false,
                    ex);

                if (!safeToRetry || attempt >= maxAttempts)
                {
                    throw connectionError;
                }

                await _delayAsync(ComputeBackoff(attempt, null), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (statusCode >= 400 && throwOnError)
            {
                ReceivifyApiException error = ReceivifyErrorFactory.Create(statusCode, rawBody, headers, label);

                bool worthRetrying = statusCode == 429 || statusCode >= 500;
                if (safeToRetry && worthRetrying && attempt < maxAttempts)
                {
                    await _delayAsync(ComputeBackoff(attempt, headers), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw error;
            }

            return new ReceivifyResponse<T>(statusCode, headers, Decode<T>(rawBody, label), rawBody);
        }
    }

    /* ================================================================ *
     * Pagination
     * ================================================================ */

    /// <summary>
    /// Follow <c>next_cursor</c> until <c>has_more</c> is false, yielding every item.
    /// </summary>
    /// <remarks>
    /// A repeated cursor throws rather than looping forever, and so does a <c>has_more: true</c>
    /// with no cursor. The API promises neither can happen; an auto-paginator that trusts that
    /// promise blindly is one bad deploy away from spinning.
    /// </remarks>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="fetchPage">Fetches one page for a given cursor.</param>
    /// <param name="startCursor">Where to start, or null for the first page.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public static async IAsyncEnumerable<T> PaginateAsync<T>(
        Func<string?, CancellationToken, Task<ReceivifyList<T>>> fetchPage,
        string? startCursor = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);

        string? cursor = startCursor;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReceivifyList<T> page = await fetchPage(cursor, cancellationToken).ConfigureAwait(false);

            foreach (T item in page.Data)
            {
                yield return item;
            }

            if (!page.HasMore)
            {
                yield break;
            }

            string? next = page.NextCursor;
            if (string.IsNullOrEmpty(next))
            {
                throw new ReceivifyException(
                    "Receivify: has_more is true but next_cursor is empty — cannot continue paging.");
            }

            if (!seen.Add(next))
            {
                throw new ReceivifyException(
                    "Receivify: pagination cursor repeated (" + next + ") — refusing to loop.");
            }

            cursor = next;
        }
    }

    /// <summary>Drain an async sequence into a list. Be mindful of unbounded result sets.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="source">The sequence to drain.</param>
    /// <param name="max">Stop after this many items.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public static async Task<List<T>> CollectAsync<T>(
        IAsyncEnumerable<T> source,
        int max = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var items = new List<T>();
        await foreach (T item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            items.Add(item);
            if (items.Count >= max)
            {
                break;
            }
        }

        return items;
    }

    /* ================================================================ *
     * Internals
     * ================================================================ */

    private static HttpClient CreateOwnedHttpClient()
    {
        // Timeout is enforced per request with a CancellationTokenSource so that a per-call
        // override is possible and so a timeout is distinguishable from a caller cancellation.
        return new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    private HttpRequestMessage BuildRequest(
        HttpMethod method,
        string url,
        string? payload,
        bool requiresAuth,
        string? idempotencyKey,
        RequestOptions? options)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        // The key is a header, never a query parameter, and never logged.
        if (requiresAuth && _apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        }

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (options?.Headers is not null)
        {
            foreach (KeyValuePair<string, string> header in options.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (payload is not null)
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static IReadOnlyDictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }

        if (response.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            }
        }

        return headers;
    }

    private static T? Decode<T>(string? rawBody, string label)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return default;
        }

        try
        {
            // Nothing here coerces a money field: every one of them is typed string and stays a
            // string all the way to the caller.
            return JsonSerializer.Deserialize<T>(rawBody, ReceivifyJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ReceivifyException(
                "Receivify " + label + " returned a body this SDK could not decode as "
                + typeof(T).Name + ": " + ex.Message,
                ex);
        }
    }

    private static string BuildQueryString(IReadOnlyList<KeyValuePair<string, string>>? query)
    {
        if (query is null || query.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in query)
        {
            builder.Append(builder.Length == 0 ? '?' : '&');
            builder.Append(Uri.EscapeDataString(pair.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }

    private TimeSpan ComputeBackoff(int attempt, IReadOnlyDictionary<string, string>? headers)
    {
        TimeSpan ceiling = _options.MaxRetryDelay;

        if (headers is not null)
        {
            // The server told us exactly when to come back; believe it.
            if (headers.TryGetValue("retry-after", out string? retryAfter)
                && double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                && seconds > 0)
            {
                return Min(TimeSpan.FromSeconds(seconds), ceiling);
            }

            if (headers.TryGetValue("x-ratelimit-reset", out string? reset)
                && long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out long resetUnix)
                && resetUnix > 0)
            {
                DateTimeOffset now = _options.UtcNow?.Invoke() ?? DateTimeOffset.UtcNow;
                TimeSpan wait = DateTimeOffset.FromUnixTimeSeconds(resetUnix) - now;
                if (wait > TimeSpan.Zero)
                {
                    return Min(wait, ceiling);
                }
            }
        }

        double baseMs = _options.RetryBaseDelay.TotalMilliseconds;
        double exponential = baseMs * Math.Pow(2, attempt - 1);
        double jitter = Random.Shared.NextDouble() * baseMs;

        return Min(TimeSpan.FromMilliseconds(exponential + jitter), ceiling);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static string RequirePathSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return Uri.EscapeDataString(value);
    }

    private static List<KeyValuePair<string, string>> QueryFrom(PageParams? page)
    {
        var query = new List<KeyValuePair<string, string>>();
        if (page is null)
        {
            return query;
        }

        if (page.Limit is int limit)
        {
            query.Add(new KeyValuePair<string, string>(
                "limit",
                limit.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrEmpty(page.Cursor))
        {
            query.Add(new KeyValuePair<string, string>("cursor", page.Cursor));
        }

        return query;
    }

    private static void AddIfPresent(List<KeyValuePair<string, string>> query, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            query.Add(new KeyValuePair<string, string>(name, value));
        }
    }

    private static void AddIfPresent(List<KeyValuePair<string, string>> query, string name, bool? value)
    {
        if (value is bool flag)
        {
            query.Add(new KeyValuePair<string, string>(name, flag ? "true" : "false"));
        }
    }

    private static List<KeyValuePair<string, string>> WithCursor(
        List<KeyValuePair<string, string>> query,
        string? cursor)
    {
        var copy = new List<KeyValuePair<string, string>>(query.Count + 1);
        foreach (KeyValuePair<string, string> pair in query)
        {
            if (!string.Equals(pair.Key, "cursor", StringComparison.Ordinal))
            {
                copy.Add(pair);
            }
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            copy.Add(new KeyValuePair<string, string>("cursor", cursor));
        }

        return copy;
    }

    private async Task<ReceivifyList<T>> ListAsync<T>(
        string path,
        List<KeyValuePair<string, string>> query,
        RequestOptions? options,
        CancellationToken cancellationToken,
        bool requiresAuth = true)
    {
        ReceivifyList<T>? page = await RequestAsync<ReceivifyList<T>>(
            HttpMethod.Get,
            path,
            query,
            body: null,
            options,
            requiresAuth,
            supportsIdempotencyKey: false,
            cancellationToken).ConfigureAwait(false);

        return page ?? new ReceivifyList<T>();
    }

    private static T Required<T>(T? value, string label)
        where T : class
    {
        return value ?? throw new ReceivifyException(
            "Receivify " + label + " returned an empty body where a resource was expected.");
    }
}
