// Receivify + ASP.NET Core minimal API: a complete, tiny shop.
//
//   GET  /                      product page with a "Pay with crypto" form
//   POST /checkout              creates the order + a Receivify invoice, redirects to checkout
//   GET  /orders/{id}           order status page
//   POST /webhooks/receivify    signed events from Receivify; the ONLY place an order is marked paid
//
// Run: set the variables from .env.example (or `dotnet user-secrets`), then `dotnet run`.

using System.Collections.Concurrent;
using System.Net;
using Receivify;

var builder = WebApplication.CreateBuilder(args);

string apiKey = builder.Configuration["RECEIVIFY_API_KEY"] ?? "";
string secret = builder.Configuration["RECEIVIFY_WEBHOOK_SECRET"] ?? "";
string apiUrl = builder.Configuration["RECEIVIFY_API_URL"] ?? "https://receivify.io";
if (apiKey == "" || secret == "")
{
    Console.Error.WriteLine("Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET (see .env.example).");
    return;
}

// One client for the app (it is thread-safe). Webhook secrets are a LIST: during a rotation,
// the new secret first and the old one second ("whsec_new,whsec_old").
builder.Services.AddSingleton(new ReceivifyClient(apiUrl, apiKey, new ReceivifyClientOptions
{
    WebhookSecrets = secret.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
}));
builder.Services.AddSingleton<OrderStore>();

var app = builder.Build();

// What we sell. Prices are strings: money never goes through a float.
var product = (Name: "Receivify T-shirt", Price: "25.00", Currency: "GBP");

// What the customer may pay with. Chain picks the network for tokens that live on several.
var coins = new Dictionary<string, (string Label, string Asset, string? Chain)>
{
    ["btc"] = ("Bitcoin", "btc", null),
    ["ltc"] = ("Litecoin", "ltc", null),
    ["eth"] = ("Ethereum", "eth", "ethereum"),
    ["usdc-base"] = ("USDC on Base", "usdc", "base"),
    ["usdt-tron"] = ("USDT on Tron", "usdt", "tron"),
    ["sol"] = ("Solana", "sol", "solana"),
};

// invoice.paid is THE signal: the full amount is confirmed and can't be reversed.
// invoice.swept follows it (funds forwarded to your wallet), so it also means paid.
var statusForEvent = new Dictionary<string, string>
{
    ["invoice.confirming"] = "confirming",
    ["invoice.partial"] = "partially_paid",
    ["invoice.paid"] = "paid",
    ["invoice.swept"] = "paid",
    ["invoice.expired"] = "expired",
    ["invoice.partial_expired"] = "underpaid",
    ["invoice.cancelled"] = "cancelled",
    ["invoice.failed"] = "failed",
};

app.MapGet("/", () =>
{
    var options = string.Join("", coins.Select(c => $"<option value=\"{c.Key}\">{WebUtility.HtmlEncode(c.Value.Label)}</option>"));
    return Html("Shop", $"""
        <h1>{product.Name}</h1><p class="price">£{product.Price}</p>
        <form method="post" action="/checkout">
          <label>Pay with <select name="coin">{options}</select></label>
          <label><input type="checkbox" name="fee_ontop" value="1"> Customer pays the fee on top</label>
          <button>Pay with crypto</button>
        </form>
        """);
});

app.MapPost("/checkout", async (HttpRequest req, ReceivifyClient receivify, OrderStore orders) =>
{
    var form = await req.ReadFormAsync();
    var coin = coins.TryGetValue(form["coin"].ToString(), out var c) ? c : coins["btc"];
    var order = orders.Create(product.Name, product.Price);

    try
    {
        var invoice = await receivify.CreateInvoiceAsync(
            new InvoiceCreateRequest
            {
                AmountFiat = product.Price,   // converted at today's rate and locked on the invoice
                Currency = product.Currency,
                Asset = coin.Asset,
                Chain = coin.Chain,
                // Comes back on every webhook as data.reference: that's how we find the order.
                Reference = order.Id,
                Metadata = new Dictionary<string, object?> { ["product"] = product.Name, ["source"] = "dotnet-example" },
                // true: the customer covers the fee so you receive the full price. null = account default.
                AddFeeOntop = form.ContainsKey("fee_ontop") ? true : null,
            },
            // Same key = same invoice: a double submit or a retry can't create two.
            new RequestOptions { IdempotencyKey = $"checkout-{order.Id}" });

        orders.SetInvoice(order.Id, invoice.Id);
        // Redirect (302) to Receivify's hosted checkout; the browser follows with a GET.
        return Results.Redirect(invoice.CheckoutUrl!, permanent: false, preserveMethod: false);
    }
    catch (ReceivifyApiException ex)
    {
        // ex.Code is stable (amount_too_low, asset_unavailable, ...); ex.Detail is for humans.
        return Html("Could not start payment", $"<p>{WebUtility.HtmlEncode(ex.Detail ?? ex.Code ?? "error")}</p><p><a href=\"/\">Back</a></p>", 400);
    }
});

app.MapPost("/webhooks/receivify", async (HttpRequest req, ReceivifyClient receivify, OrderStore orders, ILogger<Program> log) =>
{
    // Read the body as raw bytes. The signature is an HMAC over exactly these bytes; binding
    // it to a model and re-serialising would change them and fail verification.
    using var buffer = new MemoryStream();
    await req.Body.CopyToAsync(buffer);
    byte[] rawBody = buffer.ToArray();

    WebhookEvent evt;
    try
    {
        // Constant-time HMAC check + 5-minute replay window + JSON parse.
        evt = receivify.ParseWebhook(rawBody, req.Headers["X-Receivify-Signature"].ToString());
    }
    catch (ReceivifyWebhookException ex)
    {
        log.LogWarning("Rejected webhook: {Reason}", ex.Reason);
        return Results.Text("bad signature", statusCode: 400);
    }

    if (evt.Event == "test")
    {
        log.LogInformation("Test webhook received and verified.");
        return Results.Ok();
    }
    // Ignore anything that isn't about an invoice, including event names added later.
    if (!evt.Event.StartsWith("invoice.")) return Results.Ok();

    var data = evt.AsInvoiceData();
    if (data?.Reference is null) return Results.Ok(); // not created by this shop

    // Delivery is at-least-once and can be out of order; `Sequence` only increases. Apply()
    // ignores anything not newer than what was already applied, and never un-pays an order.
    if (statusForEvent.TryGetValue(evt.Event, out var next) && orders.Apply(data.Reference, evt.Sequence, next, data.Id))
    {
        log.LogInformation("Order {Id} -> {Status} ({Event})", data.Reference, next, evt.Event);
        if (next == "paid")
        {
            // Fulfil here (email, download, shipping). Keep it fast or enqueue it.
        }
    }
    return Results.Ok(); // 2xx fast; anything else is retried with backoff
});

app.MapGet("/orders/{id}", (string id, OrderStore orders) =>
{
    if (!orders.TryGet(id, out var o)) return Results.NotFound();
    // Display only. Landing on this URL proves nothing; the webhook is the source of truth.
    var refresh = o.Status == "paid" ? "" : "<meta http-equiv=\"refresh\" content=\"5\"><p class=\"muted\">Refreshes every 5 seconds.</p>";
    return Html($"Order {id}", $"""
        <h1>Order {WebUtility.HtmlEncode(o.Id)}</h1><p>{WebUtility.HtmlEncode(o.Product)}: £{o.Amount}</p>
        <p class="status {o.Status}">{o.Status}</p>{refresh}<p><a href="/">Back to the shop</a></p>
        """);
});

app.Run();

static IResult Html(string title, string body, int status = 200) => Results.Content(
    // $$""" : interpolation is {{ }}, so the CSS can use plain { } braces.
    $$"""
    <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{WebUtility.HtmlEncode(title)}}</title>
    <style>body{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}.price{font-size:2rem;font-weight:700}
    label{display:block;margin:1rem 0}button{background:#a3e635;border:0;padding:.6rem 1rem;border-radius:.6rem;font-weight:600}
    .status{font-weight:700}.paid{color:#4d7c0f}.muted{color:#71717a;font-size:.9rem}</style></head><body>{{body}}</body></html>
    """, "text/html; charset=utf-8", statusCode: status);

/// <summary>A deliberately tiny in-memory order store. Use your database in a real shop.</summary>
sealed class OrderStore
{
    public sealed class Order
    {
        public required string Id { get; init; }
        public required string Product { get; init; }
        public required string Amount { get; init; }
        public string Status { get; set; } = "pending";
        public string? InvoiceId { get; set; }
        public long LastSeq { get; set; } // highest webhook sequence applied
    }

    private readonly ConcurrentDictionary<string, Order> _orders = new();

    public Order Create(string product, string amount)
    {
        // Prefixed so it can't collide with another shop on the same Receivify account:
        // `reference` is also a de-duplication key on Receivify's side.
        var o = new Order { Id = "dotnet-" + Guid.NewGuid().ToString("N")[..8], Product = product, Amount = amount };
        _orders[o.Id] = o;
        return o;
    }

    public bool TryGet(string id, out Order order) => _orders.TryGetValue(id, out order!);

    public void SetInvoice(string id, string invoiceId)
    {
        if (_orders.TryGetValue(id, out var o)) lock (o) o.InvoiceId = invoiceId;
    }

    /// <summary>Applies an event if it is newer than the last one applied. Returns true if the status changed.</summary>
    public bool Apply(string id, long sequence, string status, string invoiceId)
    {
        if (!_orders.TryGetValue(id, out var o)) return false;
        lock (o)
        {
            if (sequence <= o.LastSeq) return false; // duplicate or stale retry
            o.LastSeq = sequence;
            if (o.Status == "paid") return false;     // paid is final for fulfilment
            o.Status = status;
            o.InvoiceId = invoiceId;
            return true;
        }
    }
}
