# Receivify example projects

Complete, runnable mini shops, one per framework. Each one is the **same flow** so you can compare them side by side:

1. **Buy**: create an order and a Receivify invoice for it (priced in fiat, the order id as `reference`), then redirect to the hosted checkout.
2. **Webhook**: read the raw body, verify `X-Receivify-Signature` with the SDK, ignore duplicates and out-of-order retries using `sequence`, and mark the order paid only on `invoice.paid`.
3. **Order page**: show what the webhook recorded. Never mark anything paid from a redirect.

Every file is commented to explain *why*, not just *what*.

| Example | Stack | SDK | Install the SDK |
|---|---|---|---|
| [node-express](node-express) | Node.js + Express | [Node.js](../node) | `npm install receivify` |
| [nextjs-app-router](nextjs-app-router) | Next.js App Router (Route Handlers) | [Node.js](../node) | `npm install receivify` |
| [python-flask](python-flask) | Python + Flask | [Python](../python) | `pip install receivify` |
| [python-fastapi](python-fastapi) | Python + FastAPI | [Python](../python) | `pip install receivify` |
| [php-laravel](php-laravel) | PHP + Laravel 11+ | [PHP](https://github.com/receivify/receivify-php) | `composer require receivify/receivify` (see the example for the GitHub fallback) |
| [plain-php](plain-php) | PHP, no framework | bundled single file | copy the folder |
| [dotnet-minimal-api](dotnet-minimal-api) | ASP.NET Core 8 minimal API | [.NET](../dotnet) | project reference (NuGet soon) |
| [go-net-http](go-net-http) | Go standard library (`net/http`) | [Go](../go) | `go get github.com/receivify/receivify-sdks/go@v1.0.0` |
| [ruby-sinatra](ruby-sinatra) (+ Rails snippet) | Ruby + Sinatra | [Ruby](../ruby) | Gemfile path (RubyGems soon) |
| [java-spring-boot](java-spring-boot) | Java 17 + Spring Boot 3 | [Java](../java) | `mvn install` from `../java` (Maven Central soon) |
| [rust-axum](rust-axum) | Rust + axum | [Rust](../rust) | Cargo path (crates.io soon) |

For C++, see the runnable programs in [`cpp/examples`](../cpp/examples) and the [C++ README](../cpp/README.md).

## Running any of them

```bash
cd examples/<name>
cp .env.example .env    # add your API key and webhook signing secret from the Receivify dashboard
# then the install + run commands from that example's README
```

The dev servers listen on `127.0.0.1` only. To receive real webhooks while developing, expose the port with a tunnel (`npx localtunnel --port <port>`, `cloudflared tunnel --url http://localhost:<port>` or `ngrok http <port>`), add the tunnel URL in **Dashboard → Webhooks**, and press **Send test**.

Each README ends with a **going live checklist**.
