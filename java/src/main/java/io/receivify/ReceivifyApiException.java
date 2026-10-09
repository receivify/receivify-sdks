package io.receivify;

import java.util.Collections;
import java.util.Map;

/**
 * The API answered with a 4xx or 5xx {@code {error, detail}} body.
 *
 * <p>Each code in the frozen registry has its own subclass ({@link NotFoundException},
 * {@link UnauthorizedException}, {@link BadRequestException}, ...). A code this SDK version does
 * not know, or a body that is not JSON at all, arrives as this base class with
 * {@link #getCode()} intact (or {@code null}), so an older SDK never breaks against a newer
 * deployment.
 *
 * <p>The API key never appears in the message, the headers or the raw body held here.
 */
public class ReceivifyApiException extends ReceivifyException {
    private static final long serialVersionUID = 1L;

    /** Everything an error response carries; passed to each typed subclass. */
    public static final class Context {
        final String code;
        final String detail;
        final int statusCode;
        final Map<String, String> headers;
        final String rawBody;
        final String request;

        public Context(String code, String detail, int statusCode, Map<String, String> headers, String rawBody, String request) {
            this.code = code;
            this.detail = detail;
            this.statusCode = statusCode;
            this.headers = headers == null ? Collections.<String, String>emptyMap() : Collections.unmodifiableMap(headers);
            this.rawBody = rawBody;
            this.request = request;
        }
    }

    private final String code;
    private final String detail;
    private final int statusCode;
    private final transient Map<String, String> headers;
    private final String rawBody;
    private final String request;

    public ReceivifyApiException(Context c) {
        super(buildMessage(c));
        this.code = c.code;
        this.detail = c.detail;
        this.statusCode = c.statusCode;
        this.headers = c.headers;
        this.rawBody = c.rawBody;
        this.request = c.request;
    }

    private static String buildMessage(Context c) {
        StringBuilder sb = new StringBuilder("Receivify ");
        if (c.request != null) {
            sb.append(c.request).append(' ');
        }
        sb.append("failed with HTTP ").append(c.statusCode);
        if (c.code != null) {
            sb.append(" ").append(c.code);
        }
        if (c.detail != null) {
            sb.append(": ").append(c.detail);
        }
        return sb.toString();
    }

    /** @return the {@code error} code, e.g. {@code not_found}; {@code null} for a non-JSON body */
    public String getCode() {
        return code;
    }

    /** @return the human-readable {@code detail}; for logs, not for branching */
    public String getDetail() {
        return detail;
    }

    /** @return the HTTP status */
    public int getStatusCode() {
        return statusCode;
    }

    /** @return response headers, keys lower-cased */
    public Map<String, String> getHeaders() {
        return headers == null ? Collections.<String, String>emptyMap() : headers;
    }

    /** @return the untouched response body */
    public String getRawBody() {
        return rawBody;
    }

    /** @return the request line, e.g. {@code GET /api/v1/invoices/{id}} */
    public String getRequest() {
        return request;
    }

    /** @return true for the codes the API documents as worth retrying, or any 5xx */
    public boolean isRetryable() {
        return ErrorCodes.isRetryable(code) || statusCode >= 500 || statusCode == 429;
    }
}
