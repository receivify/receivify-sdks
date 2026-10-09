# frozen_string_literal: true

require_relative "test_helper"
require "socket"

# Exercises the real Net::HTTP transport against a one-shot server on 127.0.0.1.
# Loopback only -- nothing leaves the machine.
class NetHTTPTransportTest < Minitest::Test
  def serve_once(response)
    server = TCPServer.new("127.0.0.1", 0)
    port = server.addr[1]
    captured = {}
    thread = Thread.new do
      sock = server.accept
      head = +""
      while (line = sock.gets) && line != "\r\n"
        head << line
      end
      captured[:head] = head
      length = head[/^content-length:\s*(\d+)/i, 1].to_i
      captured[:body] = length.positive? ? sock.read(length) : ""
      sock.write(response)
      sock.close
      server.close
    end
    [port, thread, captured]
  end

  def test_sends_headers_and_body_and_returns_error_responses_without_raising
    body = '{"error":"not_found","detail":"No such invoice."}'
    port, thread, captured = serve_once("HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\n" \
                                        "X-RateLimit-Reset: 123\r\nContent-Length: #{body.bytesize}\r\nConnection: close\r\n\r\n#{body}")
    client = Receivify::Client.new(api_url: "http://127.0.0.1:#{port}", api_key: "rcv_live_k", max_retries: 0, timeout: 5)
    err = assert_raises(Receivify::NotFoundError) { client.create_invoice(amount_sats: "1000", reference: "r1") }
    thread.join
    assert_equal 404, err.status
    assert_equal "123", err.headers["x-ratelimit-reset"]
    assert_match %r{\APOST /api/v1/invoices HTTP/1.1}, captured[:head]
    assert_match(/^Authorization: Bearer rcv_live_k\r$/i, captured[:head])
    assert_match(%r{^User-Agent: receivify-ruby/#{Regexp.escape(Receivify::VERSION)}\r$}i, captured[:head])
    assert_equal({ "amount_sats" => "1000", "reference" => "r1" }, JSON.parse(captured[:body]))
  end

  def test_bodyless_post_sends_content_length_zero
    port, thread, captured = serve_once("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}")
    client = Receivify::Client.new(api_url: "http://127.0.0.1:#{port}", api_key: "k", timeout: 5)
    client.request("POST", "/api/v1/invoices/x/cancel")
    thread.join
    assert_match(/^Content-Length: 0\r$/i, captured[:head])
    refute_match(/x-www-form-urlencoded/i, captured[:head])
  end

  def test_redirects_are_not_followed
    port, thread, = serve_once("HTTP/1.1 302 Found\r\nLocation: http://evil.example/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
    client = Receivify::Client.new(api_url: "http://127.0.0.1:#{port}", api_key: "k", timeout: 5)
    response = client.request("GET", "/api/v1/invoices/x")
    thread.join
    assert_equal 302, response.status
  end

  def test_connection_refused_is_a_transport_error
    server = TCPServer.new("127.0.0.1", 0)
    port = server.addr[1]
    server.close
    client = Receivify::Client.new(api_url: "http://127.0.0.1:#{port}", api_key: "k", max_retries: 0, timeout: 2)
    assert_raises(Receivify::TransportError) { client.get_invoice("x") }
  end
end
