package io.receivify;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/** The default {@link Transport}, on {@link java.net.http.HttpClient}. Thread-safe; share one. */
public final class JdkHttpTransport implements Transport {
    private final HttpClient http;

    /** Creates a transport with its own {@link HttpClient} (10s connect timeout, normal redirects). */
    public JdkHttpTransport() {
        this(HttpClient.newBuilder()
                .connectTimeout(Duration.ofSeconds(10))
                .followRedirects(HttpClient.Redirect.NORMAL)
                .build());
    }

    /**
     * @param http an {@link HttpClient} you own (proxy, SSL context, executor, ...)
     */
    public JdkHttpTransport(HttpClient http) {
        if (http == null) {
            throw new IllegalArgumentException("http must not be null");
        }
        this.http = http;
    }

    @Override
    public Response send(Request request) throws IOException, InterruptedException {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(request.getUrl()));
        if (request.getTimeout() != null && !request.getTimeout().isZero() && !request.getTimeout().isNegative()) {
            b.timeout(request.getTimeout());
        }
        for (Map.Entry<String, String> h : request.getHeaders().entrySet()) {
            b.header(h.getKey(), h.getValue());
        }
        HttpRequest.BodyPublisher publisher = request.getBody() == null
                ? HttpRequest.BodyPublishers.noBody()
                : HttpRequest.BodyPublishers.ofByteArray(request.getBody());
        b.method(request.getMethod(), publisher);

        HttpResponse<byte[]> response = http.send(b.build(), HttpResponse.BodyHandlers.ofByteArray());

        Map<String, String> headers = new LinkedHashMap<>();
        for (Map.Entry<String, List<String>> e : response.headers().map().entrySet()) {
            headers.put(e.getKey().toLowerCase(Locale.ROOT), String.join(", ", e.getValue()));
        }
        return new Response(response.statusCode(), headers, response.body());
    }
}
