package receivify

import (
	"errors"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// ErrorCode is a value of the frozen error-code registry, the "error" key of every failure body.
// Adding a code is an additive API change, so always keep a default branch.
type ErrorCode string

// The frozen error-code registry, in the order the API documents it.
const (
	CodeBadRequest       ErrorCode = "bad_request"       // 400 malformed request or failed validation
	CodeBadTicker        ErrorCode = "bad_ticker"        // 400 unknown or unavailable forwarding ticker
	CodeBadDestination   ErrorCode = "bad_destination"   // 400 destination not valid for the ticker/chain
	CodeAssetUnavailable ErrorCode = "asset_unavailable" // 400 settlement backend cannot accept the asset now
	CodeAmountTooLow     ErrorCode = "amount_too_low"    // 400 below the economic floor
	CodeSuspended        ErrorCode = "suspended"         // 400 merchant account suspended
	CodeLimitExceeded    ErrorCode = "limit_exceeded"    // 400 per-account quota reached
	CodeInvalidCode      ErrorCode = "invalid_code"      // 400 two-factor code not valid
	CodeNoSetup          ErrorCode = "no_setup"          // 400 two-factor enrolment not started
	CodeNotEnabled       ErrorCode = "not_enabled"       // 400 two-factor not enabled
	CodeNoTarget         ErrorCode = "no_target"         // 400 no registered active webhook endpoint matched
	CodeInvalidToken     ErrorCode = "invalid_token"     // 400 confirmation token invalid, expired or spent
	CodeUnauthorized     ErrorCode = "unauthorized"      // 401 missing, malformed, revoked or unknown API key
	CodeBadSignature     ErrorCode = "bad_signature"     // 401 inbound provider webhook failed HMAC verification
	CodeSignupDisabled   ErrorCode = "signup_disabled"   // 403 self-serve signup turned off
	CodeNotFound         ErrorCode = "not_found"         // 404 no such resource, or not yours
	CodeAlreadyExists    ErrorCode = "already_exists"    // 409 a resource with that identity already exists
	CodeAlreadyEnabled   ErrorCode = "already_enabled"   // 409 feature already on
	CodeNotAllowed       ErrorCode = "not_allowed"       // 409 resource state forbids the action
	CodeRateLimited      ErrorCode = "rate_limited"      // 429 back off
	CodeInternal         ErrorCode = "internal"          // 500 unhandled server error, retryable
	CodeUpstreamError    ErrorCode = "upstream_error"    // 502 a node or provider failed, retryable
	CodeRateUnavailable  ErrorCode = "rate_unavailable"  // 503 no fresh FX rate, retryable
)

// ErrorCodes lists every code in the frozen registry, in the documented order.
var ErrorCodes = []ErrorCode{
	CodeBadRequest, CodeBadTicker, CodeBadDestination, CodeAssetUnavailable, CodeAmountTooLow,
	CodeSuspended, CodeLimitExceeded, CodeInvalidCode, CodeNoSetup, CodeNotEnabled, CodeNoTarget,
	CodeInvalidToken, CodeUnauthorized, CodeBadSignature, CodeSignupDisabled, CodeNotFound,
	CodeAlreadyExists, CodeAlreadyEnabled, CodeNotAllowed, CodeRateLimited, CodeInternal,
	CodeUpstreamError, CodeRateUnavailable,
}

// codeError is the type of the sentinel values. An *APIError matches the sentinel for its Code
// through errors.Is; the sentinels themselves are never returned directly.
type codeError ErrorCode

func (e codeError) Error() string { return "receivify: " + string(e) }

// Sentinels for errors.Is, one per registry code.
//
//	if errors.Is(err, receivify.ErrNotFound) { ... }
var (
	ErrBadRequest       error = codeError(CodeBadRequest)
	ErrBadTicker        error = codeError(CodeBadTicker)
	ErrBadDestination   error = codeError(CodeBadDestination)
	ErrAssetUnavailable error = codeError(CodeAssetUnavailable)
	ErrAmountTooLow     error = codeError(CodeAmountTooLow)
	ErrSuspended        error = codeError(CodeSuspended)
	ErrLimitExceeded    error = codeError(CodeLimitExceeded)
	ErrInvalidCode      error = codeError(CodeInvalidCode)
	ErrNoSetup          error = codeError(CodeNoSetup)
	ErrNotEnabled       error = codeError(CodeNotEnabled)
	ErrNoTarget         error = codeError(CodeNoTarget)
	ErrInvalidToken     error = codeError(CodeInvalidToken)
	ErrUnauthorized     error = codeError(CodeUnauthorized)
	ErrBadSignature     error = codeError(CodeBadSignature)
	ErrSignupDisabled   error = codeError(CodeSignupDisabled)
	ErrNotFound         error = codeError(CodeNotFound)
	ErrAlreadyExists    error = codeError(CodeAlreadyExists)
	ErrAlreadyEnabled   error = codeError(CodeAlreadyEnabled)
	ErrNotAllowed       error = codeError(CodeNotAllowed)
	// ErrRateLimited also matches any HTTP 429, even one whose body carries no code.
	ErrRateLimited     error = codeError(CodeRateLimited)
	ErrInternal        error = codeError(CodeInternal)
	ErrUpstream        error = codeError(CodeUpstreamError)
	ErrRateUnavailable error = codeError(CodeRateUnavailable)
)

// Client-side errors that never involve the network.
var (
	// ErrMissingID is returned, before any request is made, when a required path id is empty.
	ErrMissingID = errors.New("receivify: an id is required")
	// ErrPaginationLoop is returned by an iterator when the server repeats a cursor.
	ErrPaginationLoop = errors.New("receivify: pagination cursor repeated; refusing to loop")
	// ErrPaginationNoCursor is returned by an iterator when has_more is true but next_cursor is empty.
	ErrPaginationNoCursor = errors.New("receivify: has_more is true but next_cursor is empty")
)

// APIError is an HTTP 4xx/5xx response from the API. Every failure body is
// {"error": "<code>", "detail": "<sentence>"}.
//
// Branch on Code (or errors.Is with a sentinel), never on Detail: the wording of Detail is not
// part of the API contract. A Code this SDK has never heard of is preserved verbatim.
type APIError struct {
	// Code is the raw "error" string from the body. Empty when the body had none.
	Code ErrorCode
	// Detail is the human sentence from the body, for logs.
	Detail string
	// StatusCode is the HTTP status.
	StatusCode int
	// Header holds the response headers.
	Header http.Header
	// Body is the raw response body.
	Body []byte
	// Method and Path identify the request. They never contain the API key.
	Method string
	Path   string
}

func (e *APIError) Error() string {
	var b strings.Builder
	b.WriteString("receivify: ")
	if e.Code != "" {
		b.WriteString(string(e.Code))
	} else {
		b.WriteString("api error")
	}
	b.WriteString(" (HTTP ")
	b.WriteString(strconv.Itoa(e.StatusCode))
	b.WriteString(")")
	if e.Detail != "" {
		b.WriteString(": ")
		b.WriteString(e.Detail)
	}
	if e.Method != "" {
		fmt.Fprintf(&b, " [%s %s]", e.Method, e.Path)
	}
	return b.String()
}

// Is reports whether target is the sentinel for this error's code, so errors.Is works.
func (e *APIError) Is(target error) bool {
	c, ok := target.(codeError)
	if !ok {
		return false
	}
	if ErrorCode(c) == CodeRateLimited && e.StatusCode == http.StatusTooManyRequests {
		return true
	}
	return e.Code != "" && ErrorCode(c) == e.Code
}

// Known reports whether Code is part of the registry this SDK version was built against.
func (e *APIError) Known() bool {
	for _, c := range ErrorCodes {
		if c == e.Code {
			return true
		}
	}
	return false
}

// Retryable reports whether the API documents this failure as worth retrying:
// rate_limited, internal, upstream_error, rate_unavailable, or any 5xx.
func (e *APIError) Retryable() bool {
	switch e.Code {
	case CodeRateLimited, CodeInternal, CodeUpstreamError, CodeRateUnavailable:
		return true
	}
	return e.StatusCode >= 500 || e.StatusCode == http.StatusTooManyRequests
}

// RetryAfter is the parsed Retry-After header in seconds, or 0 when absent.
func (e *APIError) RetryAfter() time.Duration {
	n := headerInt(e.Header, "Retry-After")
	if n <= 0 {
		return 0
	}
	return time.Duration(n) * time.Second
}

// RateLimitReset is the X-RateLimit-Reset header (unix seconds) as a time, or the zero time.
func (e *APIError) RateLimitReset() time.Time {
	n := headerInt(e.Header, "X-RateLimit-Reset")
	if n <= 0 {
		return time.Time{}
	}
	return time.Unix(n, 0)
}

// RateLimitLimit is the X-RateLimit-Limit header, or -1 when absent.
func (e *APIError) RateLimitLimit() int64 { return headerIntOr(e.Header, "X-RateLimit-Limit", -1) }

// RateLimitRemaining is the X-RateLimit-Remaining header, or -1 when absent.
func (e *APIError) RateLimitRemaining() int64 {
	return headerIntOr(e.Header, "X-RateLimit-Remaining", -1)
}

func headerInt(h http.Header, name string) int64 { return headerIntOr(h, name, 0) }

func headerIntOr(h http.Header, name string, def int64) int64 {
	if h == nil {
		return def
	}
	v := strings.TrimSpace(h.Get(name))
	if v == "" {
		return def
	}
	n, err := strconv.ParseInt(v, 10, 64)
	if err != nil {
		f, ferr := strconv.ParseFloat(v, 64)
		if ferr != nil {
			return def
		}
		return int64(f)
	}
	return n
}

// ConnectionError means the request never produced an HTTP response: DNS, TLS, a socket
// error, a cancelled context, or this SDK's own per-request timeout.
type ConnectionError struct {
	Method string
	Path   string
	// Timeout is true when the configured per-request timeout fired.
	Timeout bool
	// Err is the underlying transport error. errors.Is(err, context.Canceled) works through it.
	Err error
}

func (e *ConnectionError) Error() string {
	if e.Timeout {
		return fmt.Sprintf("receivify: %s %s timed out: %v", e.Method, e.Path, e.Err)
	}
	return fmt.Sprintf("receivify: %s %s failed: %v", e.Method, e.Path, e.Err)
}

func (e *ConnectionError) Unwrap() error { return e.Err }
