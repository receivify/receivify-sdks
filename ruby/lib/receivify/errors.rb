# frozen_string_literal: true

require "json"

module Receivify
  # Base class for every error this SDK raises.
  #
  #   Receivify::Error
  #    +- Receivify::TransportError           the request never produced an HTTP response
  #    |   +- Receivify::TimeoutError
  #    +- Receivify::SignatureVerificationError  a webhook signature did not verify
  #    +- Receivify::WebhookPayloadError       a verified webhook body was not a JSON object
  #    +- Receivify::APIError                  the API answered with a 4xx/5xx
  #        +- Receivify::BadRequestError       ... one subclass per frozen error code
  #        +- Receivify::UnauthorizedError
  #        +- Receivify::NotFoundError
  #        +- ...
  class Error < StandardError; end

  # The request never produced an HTTP response (DNS, TLS, connection refused, reset).
  class TransportError < Error
    def initialize(message = "Receivify request failed.", cause: nil)
      super(message)
      @original_cause = cause
    end

    # The low-level exception that caused this one, if any.
    def original_cause
      @original_cause || cause
    end
  end

  # The request exceeded the configured timeout.
  class TimeoutError < TransportError; end

  # A webhook signature did not verify against any of the supplied secrets.
  class SignatureVerificationError < Error; end

  # A webhook body verified but was not a JSON object.
  class WebhookPayloadError < Error; end

  # The API answered with a 4xx or 5xx.
  #
  # Also the fallback for an `error` code this SDK build does not recognise: the raw code is
  # always available as #code, so a code added to the API later never crashes an older SDK.
  class APIError < Error
    # The raw `error` code (e.g. "not_found"), or nil when the body was not an
    # `{error, detail}` object (an HTML page from a proxy, for example).
    attr_reader :code
    # The human sentence. For logs and developers -- never branch on the wording.
    attr_reader :detail
    # The HTTP status code.
    attr_reader :status
    # Response headers (lower-cased keys), for Retry-After / X-RateLimit-Reset.
    attr_reader :headers
    # The raw response body (truncated to 500 characters) when it could not be parsed.
    attr_reader :body

    def initialize(code = nil, detail = "", status: 0, headers: nil, body: nil)
      @code = code
      @detail = detail.to_s
      @status = status.to_i
      @headers = (headers || {}).each_with_object({}) { |(k, v), h| h[k.to_s.downcase] = v.to_s }.freeze
      @body = body
      super(format("%s (%d %s)", @detail.empty? ? "Receivify API error" : @detail, @status, code || "no code"))
    end

    # Codes whose subclass overrides this are worth retrying as-is.
    def retryable?
      false
    end

    # Seconds the server asked us to wait, from Retry-After.
    def retry_after
      header_int("retry-after")
    end

    # Unix seconds at which the current rate-limit window resets.
    def rate_limit_reset
      header_int("x-ratelimit-reset")
    end

    def inspect
      "#<#{self.class.name} code=#{code.inspect} status=#{status} detail=#{detail.inspect}>"
    end

    private

    def header_int(name)
      value = @headers[name]
      return nil if value.nil? || value.strip !~ /\A-?\d+\z/

      value.strip.to_i
    end
  end

  # Mixin for retryable error classes.
  module RetryableError
    def retryable?
      true
    end
  end

  # 400 -- malformed request: bad JSON, a missing field, or a failed validation.
  class BadRequestError < APIError; end
  # 400 -- the forwarding ticker is unknown, misspelled, or not offered here.
  class BadTickerError < APIError; end
  # 400 -- the destination address is not valid for that ticker/chain.
  class BadDestinationError < APIError; end
  # 400 -- the asset is enabled but its settlement backend cannot accept it now.
  class AssetUnavailableError < APIError; end
  # 400 -- below the economic floor: forwarding gas would exceed the fee earned.
  class AmountTooLowError < APIError; end
  # 400 -- the merchant account is suspended and cannot create new resources.
  class SuspendedError < APIError; end
  # 400 -- a per-account quota for this resource is already at its maximum.
  class LimitExceededError < APIError; end
  # 400 -- the supplied two-factor (TOTP) code is not valid.
  class InvalidCodeError < APIError; end
  # 400 -- two-factor enrolment has not been started yet.
  class NoSetupError < APIError; end
  # 400 -- two-factor authentication is not enabled on this account.
  class NotEnabledError < APIError; end
  # 400 -- no registered, active webhook endpoint matched the request.
  class NoTargetError < APIError; end
  # 400 -- a single-use confirmation token is invalid, expired, or already spent.
  class InvalidTokenError < APIError; end
  # 401 -- missing, malformed, revoked or unknown API key.
  class UnauthorizedError < APIError; end
  # 401 -- an inbound provider webhook failed HMAC verification. Never returned to merchants.
  class BadSignatureError < APIError; end
  # 403 -- self-serve signup is turned off on this deployment.
  class SignupDisabledError < APIError; end
  # 404 -- no such resource, or it does not belong to the caller (deliberately indistinguishable).
  class NotFoundError < APIError; end
  # 409 -- a resource with that unique identity already exists.
  class AlreadyExistsError < APIError; end
  # 409 -- the feature being enabled is already on.
  class AlreadyEnabledError < APIError; end
  # 409 -- the resource exists but its state forbids this action.
  class NotAllowedError < APIError; end

  # 429 -- back off using Retry-After / X-RateLimit-Reset.
  class RateLimitedError < APIError
    include RetryableError
  end

  # 500 -- an unhandled server error. Safe to retry; report it if it persists.
  class InternalError < APIError
    include RetryableError
  end

  # 502 -- a node or payment provider we depend on failed. Retryable.
  class UpstreamError < APIError
    include RetryableError
  end

  # 503 -- no fresh FX rate is available to price the request. Retryable.
  class RateUnavailableError < APIError
    include RetryableError
  end

  # Frozen error code => exception class, in the order of the enum in openapi.yaml.
  # A code missing from this table falls back to Receivify::APIError.
  ERROR_CODES = {
    "bad_request" => BadRequestError,
    "bad_ticker" => BadTickerError,
    "bad_destination" => BadDestinationError,
    "asset_unavailable" => AssetUnavailableError,
    "amount_too_low" => AmountTooLowError,
    "suspended" => SuspendedError,
    "limit_exceeded" => LimitExceededError,
    "invalid_code" => InvalidCodeError,
    "no_setup" => NoSetupError,
    "not_enabled" => NotEnabledError,
    "no_target" => NoTargetError,
    "invalid_token" => InvalidTokenError,
    "unauthorized" => UnauthorizedError,
    "bad_signature" => BadSignatureError,
    "signup_disabled" => SignupDisabledError,
    "not_found" => NotFoundError,
    "already_exists" => AlreadyExistsError,
    "already_enabled" => AlreadyEnabledError,
    "not_allowed" => NotAllowedError,
    "rate_limited" => RateLimitedError,
    "internal" => InternalError,
    "upstream_error" => UpstreamError,
    "rate_unavailable" => RateUnavailableError
  }.freeze

  DEFAULT_ERROR_DETAIL = {
    400 => "The request was rejected.",
    401 => "Unauthorized.",
    403 => "Forbidden.",
    404 => "Not found.",
    409 => "Not allowed in the resource's current state.",
    429 => "Rate limited.",
    500 => "Unexpected error.",
    502 => "Upstream error.",
    503 => "Service unavailable."
  }.freeze
  private_constant :DEFAULT_ERROR_DETAIL

  class << self
    # The exception class for an error code, or Receivify::APIError when it is unknown.
    def error_class_for_code(code)
      return APIError if code.nil? || code.to_s.empty?

      ERROR_CODES.fetch(code.to_s, APIError)
    end

    # Build the right exception for an error response. Never raises while doing so: a body
    # that is not an `{error, detail}` object still yields a usable APIError with code nil.
    def error_from_response(status, headers, raw_body)
      payload = begin
        raw_body.nil? || raw_body.empty? ? nil : JSON.parse(raw_body)
      rescue JSON::ParserError
        nil
      end
      code = nil
      detail = ""
      if payload.is_a?(Hash)
        code = payload["error"] if payload["error"].is_a?(String)
        detail = payload["detail"] if payload["detail"].is_a?(String)
      end
      detail = DEFAULT_ERROR_DETAIL.fetch(status.to_i, "Receivify request failed.") if detail.empty?
      error_class_for_code(code).new(
        code, detail,
        status: status, headers: headers,
        body: raw_body.nil? || raw_body.empty? ? nil : raw_body[0, 500]
      )
    end
  end
end
