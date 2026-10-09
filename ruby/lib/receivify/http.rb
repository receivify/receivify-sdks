# frozen_string_literal: true

require "net/http"
require "openssl"
require "uri"
require "json"

module Receivify
  # One outbound HTTP request, fully built.
  #
  # The API key lives in headers["Authorization"] and is NEVER in #url: URLs end up in proxy
  # logs, access logs and error trackers. #inspect redacts the Authorization header.
  class Request
    attr_reader :method, :url, :headers, :body, :timeout

    def initialize(method:, url:, headers: {}, body: nil, timeout: 30)
      @method = method.to_s.upcase.freeze
      @url = url.to_s.freeze
      @headers = headers.dup.freeze
      @body = body.nil? ? nil : body.to_s.dup.freeze
      @timeout = timeout
      freeze
    end

    # Case-insensitive header lookup.
    def header(name)
      key = @headers.keys.find { |k| k.to_s.casecmp?(name.to_s) }
      key.nil? ? nil : @headers[key]
    end

    def inspect
      safe = @headers.each_with_object({}) do |(k, v), h|
        h[k] = k.to_s.casecmp?("authorization") ? "<redacted>" : v
      end
      "#<Receivify::Request #{@method} #{@url} headers=#{safe.inspect} body=#{@body.inspect}>"
    end
    alias to_s inspect

    def pretty_print(q)
      q.text(inspect)
    end
  end

  # One inbound HTTP response. Header lookup via #header is case-insensitive.
  class Response
    attr_reader :status, :headers, :body

    def initialize(status, headers = {}, body = "")
      @status = status.to_i
      @headers = (headers || {}).each_with_object({}) do |(k, v), h|
        h[k.to_s.downcase] = v.is_a?(Array) ? v.join(", ") : v.to_s
      end.freeze
      @body = body.to_s.dup.force_encoding(Encoding::UTF_8).freeze
      freeze
    end

    def header(name)
      @headers[name.to_s.downcase]
    end

    # Decode the body as JSON (nil for an empty body). Raises JSON::ParserError otherwise.
    def json
      return nil if @body.empty?

      JSON.parse(@body)
    end
  end

  # The default transport: Net::HTTP from the standard library.
  #
  # A transport is anything that responds to `call(request)` and returns a Receivify::Response.
  # It MUST NOT raise on a 4xx/5xx (return the response and let the client map it to a typed
  # error). It raises Receivify::TransportError only when no response was produced at all.
  # A lambda works, which is how the test suite runs without a network.
  #
  # Redirects are never followed: following a 30x would re-send the Bearer key to whatever
  # host the Location header names.
  class NetHTTPTransport
    METHODS = {
      "GET" => Net::HTTP::Get,
      "POST" => Net::HTTP::Post,
      "PATCH" => Net::HTTP::Patch,
      "PUT" => Net::HTTP::Put,
      "DELETE" => Net::HTTP::Delete,
      "HEAD" => Net::HTTP::Head
    }.freeze

    # ca_file:     a custom CA bundle path (corporate proxies, self-hosted deployments).
    # verify_mode: an OpenSSL::SSL::VERIFY_* constant. Leave it alone in production.
    def initialize(ca_file: nil, verify_mode: nil)
      @ca_file = ca_file
      @verify_mode = verify_mode
    end

    def call(request)
      uri = URI.parse(request.url)
      klass = METHODS.fetch(request.method) { raise ArgumentError, "Unsupported HTTP method #{request.method}" }
      http = Net::HTTP.new(uri.host, uri.port)
      http.use_ssl = (uri.scheme == "https")
      http.ca_file = @ca_file if @ca_file
      http.verify_mode = @verify_mode if @verify_mode
      timeout = request.timeout.to_f
      http.open_timeout = timeout
      http.read_timeout = timeout
      http.write_timeout = timeout if http.respond_to?(:write_timeout=)
      http.ssl_timeout = timeout if http.use_ssl?

      req = klass.new(uri.request_uri)
      request.headers.each { |k, v| req[k] = v }
      if request.body
        req.body = request.body
      elsif req.request_body_permitted?
        # A bodyless POST (cancel, revoke, rotate). Net::HTTP would otherwise send an empty
        # body labelled application/x-www-form-urlencoded.
        req.body = ""
        req["Content-Type"] ||= "application/json"
      end

      raw = http.start { |conn| conn.request(req) }
      headers = {}
      raw.each_header { |k, v| headers[k] = v }
      Response.new(raw.code.to_i, headers, raw.body.to_s)
    rescue Timeout::Error => e
      raise Receivify::TimeoutError.new("Receivify request timed out.", cause: e)
    rescue SocketError, SystemCallError, IOError, EOFError, OpenSSL::SSL::SSLError,
           Net::HTTPBadResponse, Net::ProtocolError, Zlib::Error => e
      raise Receivify::TransportError.new("Receivify request failed: #{e.class}: #{e.message}", cause: e)
    end
  end
end
