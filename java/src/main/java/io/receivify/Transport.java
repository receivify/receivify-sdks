package io.receivify;

import java.io.IOException;
import java.time.Duration;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;

/**
 * The HTTP seam. The default is {@link JdkHttpTransport} (built on {@code java.net.http}); pass
 * your own to {@link ReceivifyClient.Builder#transport(Transport)} to route through another HTTP
 * stack, add tracing, or run the SDK fully offline in tests.
 *
 * <p>Implementations should throw {@link java.net.http.HttpTimeoutException} (or any
 * {@link java.net.SocketTimeoutException}) on a timeout so the SDK can report it as one.
 */
@FunctionalInterface
public interface Transport {

    /**
     * Perform one HTTP exchange. Must not throw for 4xx/5xx; return them as a response.
     *
     * @param request the request
     * @return the response
     * @throws IOException on a transport failure
     * @throws InterruptedException when the calling thread is interrupted
     */
    Response send(Request request) throws IOException, InterruptedException;

    /** An outgoing request. Header names are as sent; the body is {@code null} for none. */
    final class Request {
        private final String method;
        private final String url;
        private final Map<String, String> headers;
        private final byte[] body;
        private final Duration timeout;

        public Request(String method, String url, Map<String, String> headers, byte[] body, Duration timeout) {
            this.method = method;
            this.url = url;
            this.headers = Collections.unmodifiableMap(new LinkedHashMap<>(headers));
            this.body = body;
            this.timeout = timeout;
        }

        public String getMethod() {
            return method;
        }

        public String getUrl() {
            return url;
        }

        public Map<String, String> getHeaders() {
            return headers;
        }

        /**
         * @param name a header name, matched case-insensitively
         * @return its value, or {@code null}
         */
        public String getHeader(String name) {
            for (Map.Entry<String, String> e : headers.entrySet()) {
                if (e.getKey().equalsIgnoreCase(name)) {
                    return e.getValue();
                }
            }
            return null;
        }

        /** @return the body bytes, or {@code null} */
        public byte[] getBody() {
            return body;
        }

        public Duration getTimeout() {
            return timeout;
        }

        /** Never prints the Authorization header. */
        @Override
        public String toString() {
            return method + " " + url;
        }
    }

    /** An HTTP response. Header names are lower-cased. */
    final class Response {
        private final int statusCode;
        private final Map<String, String> headers;
        private final byte[] body;

        public Response(int statusCode, Map<String, String> headers, byte[] body) {
            this.statusCode = statusCode;
            Map<String, String> lower = new LinkedHashMap<>();
            if (headers != null) {
                for (Map.Entry<String, String> e : headers.entrySet()) {
                    lower.put(e.getKey().toLowerCase(Locale.ROOT), e.getValue());
                }
            }
            this.headers = Collections.unmodifiableMap(lower);
            this.body = body == null ? new byte[0] : body;
        }

        public int getStatusCode() {
            return statusCode;
        }

        public Map<String, String> getHeaders() {
            return headers;
        }

        public byte[] getBody() {
            return body;
        }
    }
}
