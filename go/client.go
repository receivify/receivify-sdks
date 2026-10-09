package receivify

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math/rand"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Version is the SDK version, also sent in the User-Agent header.
const Version = "1.0.0"

// UserAgent is the identity this SDK sends on every request.
const UserAgent = "receivify-go/" + Version

const (
	// DefaultTimeout is the per-attempt request timeout.
	DefaultTimeout = 30 * time.Second
	// DefaultMaxRetries is how many times a retry-safe request is retried.
	DefaultMaxRetries = 2
	// DefaultRetryBackoff is the first backoff step; it doubles each attempt.
	DefaultRetryBackoff = 500 * time.Millisecond
	maxRetryDelay       = 20 * time.Second
)

// Client talks to one Receivify deployment. It is safe for concurrent use.
//
// The API key is held in an unexported field and is never written to a URL, a log line,
// an error message, or the output of fmt.
type Client struct {
	apiURL         string
	apiKey         string
	httpClient     *http.Client
	timeout        time.Duration
	maxRetries     int
	retryBackoff   time.Duration
	userAgent      string
	webhookSecrets []string

	// Overridable in tests.
	sleep func(ctx context.Context, d time.Duration) error
	now   func() time.Time
}

// Option configures a Client.
type Option func(*Client)

// WithTimeout sets the per-attempt timeout. Zero or negative disables the SDK timeout
// (the context deadline, if any, still applies). Default 30s.
func WithTimeout(d time.Duration) Option { return func(c *Client) { c.timeout = d } }

// WithHTTPClient supplies the *http.Client used for every request: proxies, custom TLS,
// instrumentation, or an injected http.RoundTripper in tests.
func WithHTTPClient(hc *http.Client) Option {
	return func(c *Client) {
		if hc != nil {
			c.httpClient = hc
		}
	}
}

// WithWebhookSecrets sets the secrets Client.VerifyWebhook and Client.ParseWebhook check:
// current secret first, the previous one second during a rotation. Empty strings are dropped.
func WithWebhookSecrets(secrets ...string) Option {
	return func(c *Client) { c.webhookSecrets = normaliseSecrets(secrets) }
}

// WithMaxRetries sets how many times a retry-safe request (a GET, or a mutating call that
// carries an Idempotency-Key on an endpoint that supports one) is retried on 429, 5xx or a
// transport failure. Default 2. Zero disables retries.
func WithMaxRetries(n int) Option {
	return func(c *Client) {
		if n >= 0 {
			c.maxRetries = n
		}
	}
}

// WithRetryBackoff sets the first backoff step (doubled each attempt, plus jitter) used when
// the server sends neither Retry-After nor X-RateLimit-Reset. Default 500ms.
func WithRetryBackoff(d time.Duration) Option {
	return func(c *Client) {
		if d >= 0 {
			c.retryBackoff = d
		}
	}
}

// WithUserAgent appends a suffix (e.g. "my-shop/2.1") to the SDK's own User-Agent.
func WithUserAgent(suffix string) Option {
	return func(c *Client) {
		if s := strings.TrimSpace(suffix); s != "" {
			c.userAgent = UserAgent + " " + s
		}
	}
}

// NewClient builds a client for the deployment at apiURL (e.g. "https://receivify.io").
// apiKey is the merchant API key; it may be empty if you only call public routes.
func NewClient(apiURL, apiKey string, opts ...Option) (*Client, error) {
	base := strings.TrimRight(strings.TrimSpace(apiURL), "/")
	if base == "" {
		return nil, errors.New(`receivify: apiURL is required, e.g. "https://receivify.io"`)
	}
	u, err := url.Parse(base)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return nil, fmt.Errorf("receivify: apiURL must be an absolute http(s) URL, got %q", apiURL)
	}
	if u.User != nil || u.RawQuery != "" {
		return nil, errors.New("receivify: apiURL must not contain credentials or a query string")
	}
	c := &Client{
		apiURL:       base,
		apiKey:       strings.TrimSpace(apiKey),
		httpClient:   &http.Client{},
		timeout:      DefaultTimeout,
		maxRetries:   DefaultMaxRetries,
		retryBackoff: DefaultRetryBackoff,
		userAgent:    UserAgent,
		sleep:        sleepCtx,
		now:          time.Now,
	}
	for _, opt := range opts {
		if opt != nil {
			opt(c)
		}
	}
	return c, nil
}

// APIURL is the base URL this client talks to.
func (c *Client) APIURL() string { return c.apiURL }

// String never includes the API key.
func (c *Client) String() string {
	return fmt.Sprintf("receivify.Client{apiURL: %q, version: %q}", c.apiURL, Version)
}

// GoString never includes the API key, so %#v is safe too.
func (c *Client) GoString() string { return c.String() }

// RequestOptions are per-call settings. Pass at most one as the trailing argument of any
// network method:
//
//	client.CreateInvoice(ctx, params, receivify.RequestOptions{IdempotencyKey: "order-42"})
type RequestOptions struct {
	// IdempotencyKey is sent as the Idempotency-Key header. On endpoints that support it
	// (creating invoices, addresses, payment links, recurring schedules, API keys, webhook
	// endpoints, and rotating the webhook secret) a retry with the same key returns the
	// original result instead of acting twice, and it makes the request safe for this SDK to
	// retry automatically.
	IdempotencyKey string
	// Header adds extra request headers. Authorization and User-Agent cannot be overridden.
	Header http.Header
}

// Response is the HTTP status and headers of a completed call, returned by Client.Do.
type Response struct {
	StatusCode int
	Header     http.Header
}

// IdempotentReplay reports whether the server answered from its idempotency store.
func (r *Response) IdempotentReplay() bool {
	return r != nil && strings.EqualFold(r.Header.Get("Idempotent-Replay"), "true")
}

type call struct {
	method string
	path   string // already escaped, starting with "/"
	query  url.Values
	body   any
	noAuth bool
	// idempotent is true when the endpoint declares Idempotency-Key support in openapi.yaml.
	// Only then does a supplied key make a mutating request retry-safe.
	idempotent bool
	// noRetry disables automatic retries (used where a 5xx is an answer, not a failure).
	noRetry bool
	opts    []RequestOptions
}

// Do issues an arbitrary request against the API and decodes a JSON success body into out
// (which may be nil). It is the escape hatch for endpoints this SDK does not wrap yet.
// path must start with "/" and be already escaped. Only GET requests are retried.
func (c *Client) Do(ctx context.Context, method, path string, body, out any, opts ...RequestOptions) (*Response, error) {
	if !strings.HasPrefix(path, "/") {
		return nil, fmt.Errorf("receivify: path must start with /, got %q", path)
	}
	u, err := url.Parse(path)
	if err != nil {
		return nil, fmt.Errorf("receivify: bad path %q: %w", path, err)
	}
	return c.do(ctx, &call{method: strings.ToUpper(method), path: u.EscapedPath(), query: u.Query(), body: body, opts: opts}, out)
}

func (c *Client) do(ctx context.Context, cl *call, out any) (*Response, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	target := c.apiURL + cl.path
	if len(cl.query) > 0 {
		target += "?" + cl.query.Encode()
	}

	var payload []byte
	if cl.body != nil {
		var buf bytes.Buffer
		enc := json.NewEncoder(&buf)
		enc.SetEscapeHTML(false)
		if err := enc.Encode(cl.body); err != nil {
			return nil, fmt.Errorf("receivify: encoding request body: %w", err)
		}
		payload = bytes.TrimRight(buf.Bytes(), "\n")
	}

	var key string
	var extra http.Header
	for _, o := range cl.opts {
		if o.IdempotencyKey != "" {
			key = o.IdempotencyKey
		}
		for k, vs := range o.Header {
			if extra == nil {
				extra = http.Header{}
			}
			for _, v := range vs {
				extra.Add(k, v)
			}
		}
	}

	// Retry safety: a GET is safe. A mutating call is safe ONLY when the endpoint declares
	// Idempotency-Key support AND the caller supplied one. Blind-retrying a POST is how a
	// customer gets charged twice.
	safe := !cl.noRetry && (cl.method == http.MethodGet || (cl.idempotent && key != ""))
	maxAttempts := 1
	if safe {
		maxAttempts = c.maxRetries + 1
	}

	for attempt := 1; ; attempt++ {
		var bodyReader io.Reader
		if payload != nil {
			bodyReader = bytes.NewReader(payload)
		}
		attemptCtx, cancel := ctx, context.CancelFunc(func() {})
		if c.timeout > 0 {
			attemptCtx, cancel = context.WithTimeout(ctx, c.timeout)
		}
		req, err := http.NewRequestWithContext(attemptCtx, cl.method, target, bodyReader)
		if err != nil {
			cancel()
			return nil, fmt.Errorf("receivify: building request: %w", err)
		}
		for k, vs := range extra {
			for _, v := range vs {
				req.Header.Add(k, v)
			}
		}
		req.Header.Set("Accept", "application/json")
		req.Header.Set("User-Agent", c.userAgent)
		if payload != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		if key != "" {
			req.Header.Set("Idempotency-Key", key)
		}
		req.Header.Del("Authorization")
		if !cl.noAuth && c.apiKey != "" {
			req.Header.Set("Authorization", "Bearer "+c.apiKey)
		}

		resp, err := c.httpClient.Do(req)
		var raw []byte
		if err == nil {
			raw, err = io.ReadAll(resp.Body)
			resp.Body.Close()
		}
		timedOut := err != nil && ctx.Err() == nil && errors.Is(attemptCtx.Err(), context.DeadlineExceeded)
		cancel()

		if err != nil {
			cerr := &ConnectionError{Method: cl.method, Path: cl.path, Timeout: timedOut, Err: stripURLError(err)}
			if ctx.Err() != nil || !safe || attempt >= maxAttempts {
				return nil, cerr
			}
			if serr := c.sleep(ctx, c.backoff(attempt, nil)); serr != nil {
				return nil, cerr
			}
			continue
		}

		if resp.StatusCode >= 400 {
			apiErr := newAPIError(resp.StatusCode, resp.Header, raw, cl.method, cl.path)
			worth := resp.StatusCode == http.StatusTooManyRequests || resp.StatusCode >= 500
			if safe && worth && attempt < maxAttempts {
				if serr := c.sleep(ctx, c.backoff(attempt, resp.Header)); serr != nil {
					return nil, apiErr
				}
				continue
			}
			return nil, apiErr
		}

		meta := &Response{StatusCode: resp.StatusCode, Header: resp.Header}
		if out != nil && len(bytes.TrimSpace(raw)) > 0 {
			if err := decodeJSON(raw, out); err != nil {
				return meta, fmt.Errorf("receivify: decoding %s %s response (HTTP %d): %w", cl.method, cl.path, resp.StatusCode, err)
			}
		}
		return meta, nil
	}
}

// decodeJSON decodes with UseNumber so a number inside free-form metadata stays a json.Number
// rather than becoming a lossy float64.
func decodeJSON(raw []byte, out any) error {
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.UseNumber()
	return dec.Decode(out)
}

func newAPIError(status int, h http.Header, raw []byte, method, path string) *APIError {
	e := &APIError{StatusCode: status, Header: h, Body: raw, Method: method, Path: path}
	var body struct {
		Error  any `json:"error"`
		Detail any `json:"detail"`
	}
	if json.Unmarshal(raw, &body) == nil {
		if s, ok := body.Error.(string); ok {
			e.Code = ErrorCode(s)
		}
		if s, ok := body.Detail.(string); ok {
			e.Detail = s
		}
	}
	return e
}

// stripURLError unwraps *url.Error so the full URL is not repeated in the message; the
// method and path are already on the ConnectionError.
func stripURLError(err error) error {
	var ue *url.Error
	if errors.As(err, &ue) && ue.Err != nil {
		return ue.Err
	}
	return err
}

// backoff honours Retry-After, then X-RateLimit-Reset, then falls back to exponential
// backoff with jitter. Capped at 20s.
func (c *Client) backoff(attempt int, h http.Header) time.Duration {
	if h != nil {
		if n := headerInt(h, "Retry-After"); n > 0 {
			return minDuration(time.Duration(n)*time.Second, maxRetryDelay)
		}
		if n := headerInt(h, "X-RateLimit-Reset"); n > 0 {
			if wait := time.Unix(n, 0).Sub(c.now()); wait > 0 {
				return minDuration(wait, maxRetryDelay)
			}
		}
	}
	d := c.retryBackoff << uint(attempt-1)
	if c.retryBackoff > 0 {
		d += time.Duration(rand.Int63n(int64(c.retryBackoff)))
	}
	return minDuration(d, maxRetryDelay)
}

func minDuration(a, b time.Duration) time.Duration {
	if a < b {
		return a
	}
	return b
}

func sleepCtx(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return ctx.Err()
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

// seg escapes one path segment and rejects an empty id before any request is made.
func seg(id string) (string, error) {
	if strings.TrimSpace(id) == "" {
		return "", ErrMissingID
	}
	return url.PathEscape(id), nil
}

// Bool returns a pointer to b, for optional boolean fields such as
// CreateInvoiceParams.AddFeeOntop where "omitted" and "false" mean different things.
func Bool(b bool) *bool { return &b }

// String returns a pointer to s, for optional string fields where an empty string is meaningful.
func String(s string) *string { return &s }
