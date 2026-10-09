using System.Text.Json;

namespace Receivify.Tests;

internal static class ClientTests
{
    // A wei value with 19 significant digits: it does not survive a double, and it is exactly why
    // every money field in this SDK is a string.
    private const string BigWei = "1234567890123456789";

    private const string InvoiceJson = """
        {
          "id": "6f1b0f6a-0000-4000-8000-000000000001",
          "reference": "order-42",
          "asset": "eth",
          "amount_expected": "1234567890123456789",
          "amount_received": "0",
          "amount_fiat": "25.00",
          "fiat_currency": "USD",
          "chain": "ethereum",
          "provider": "evm",
          "address": "0xdeadbeef",
          "status": "pending",
          "expires_at": "2026-08-25T12:00:00.000Z",
          "paid_at": null,
          "created_at": "2026-08-25T11:00:00.000Z",
          "metadata": { "cart": "abc" },
          "checkout_url": "https://receivify.test/pay/6f1b0f6a"
        }
        """;

    private const string ForwardJson = """
        {
          "id": "11111111-2222-4333-8444-555555555555",
          "forward_id": "11111111-2222-4333-8444-555555555555",
          "ticker": "btc",
          "provider": "bitcoind",
          "chain": null,
          "address": "bc1qdeposit",
          "destination": "bc1qdestination",
          "callback_url": "https://merchant.test/hook",
          "confirmations_required": 2,
          "min_forward": "0.00050000",
          "status": "active",
          "expires_at": null,
          "created_at": "2026-08-25T11:00:00.000Z",
          "updated_at": null
        }
        """;

    public static async Task RunAsync()
    {
        Test.Section("HTTP, auth and headers");

        await Test.RunAsync("the API key travels in the Authorization header and never in the URL", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk(InvoiceJson);

            await client.GetInvoiceAsync("6f1b0f6a-0000-4000-8000-000000000001").ConfigureAwait(false);

            RecordedRequest request = transport.Last;
            Test.AreEqual("Bearer " + TestClient.ApiKey, request.Header("Authorization"), "bearer header");
            Test.IsFalse(
                request.Uri.ToString().Contains(TestClient.ApiKey, StringComparison.Ordinal),
                "the key must never appear in a URL");
            Test.IsTrue(
                request.Header("User-Agent")!.StartsWith("receivify-dotnet/", StringComparison.Ordinal),
                "User-Agent is receivify-dotnet/<version>");
        }).ConfigureAwait(false);

        await Test.RunAsync("public catalogue routes send no Authorization header", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""{"data":[],"has_more":false,"next_cursor":null}""");

            await client.ListTickersAsync().ConfigureAwait(false);

            Test.IsNull(transport.Last.Header("Authorization"), "no bearer on a public route");
        }).ConfigureAwait(false);

        await Test.RunAsync("Idempotency-Key is sent when supplied", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(201, InvoiceJson);

            await client.CreateInvoiceAsync(
                new InvoiceCreateRequest { AmountSats = "1000" },
                new RequestOptions { IdempotencyKey = "key-abc" }).ConfigureAwait(false);

            Test.AreEqual("key-abc", transport.Last.Header("Idempotency-Key"), "idempotency header");
        }).ConfigureAwait(false);

        await Test.RunAsync("query parameters are built and encoded", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""{"data":[],"has_more":false,"next_cursor":null}""");

            await client.ListInvoicesAsync(new InvoiceListParams
            {
                Limit = 25,
                Cursor = "cur/sor+1",
                Status = InvoiceStatus.Paid,
            }).ConfigureAwait(false);

            string url = transport.Last.Uri.ToString();
            Test.IsTrue(url.Contains("limit=25", StringComparison.Ordinal), "limit is present");
            Test.IsTrue(url.Contains("status=paid", StringComparison.Ordinal), "status is present");
            Test.IsTrue(url.Contains("cur%2Fsor%2B1", StringComparison.Ordinal), "the cursor is percent-encoded");
        }).ConfigureAwait(false);

        Test.Section("Money stays a string");

        await Test.RunAsync("a 19-digit wei amount round-trips exactly", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk(InvoiceJson);

            Invoice invoice = await client
                .GetInvoiceAsync("6f1b0f6a-0000-4000-8000-000000000001")
                .ConfigureAwait(false);

            Test.AreEqual(BigWei, invoice.AmountExpected, "amount_expected is the untouched string");
            Test.AreEqual("0", invoice.AmountReceived, "amount_received is the untouched string");
            Test.AreEqual("25.00", invoice.AmountFiat, "amount_fiat is a decimal STRING, not a number");
            Test.AreEqual(
                BigWei,
                Money.ToBigInteger(invoice.AmountExpected).ToString(),
                "and it survives an exact BigInteger round trip");
        }).ConfigureAwait(false);

        await Test.RunAsync("a money field arriving as a JSON number keeps its exact digits", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""
                {
                  "id": "x",
                  "status": "pending",
                  "address": "bc1q",
                  "amount_expected": 1234567890123456789,
                  "amount_received": 0,
                  "expires_at": null
                }
                """);

            PublicInvoice invoice = await client.GetPublicInvoiceAsync("x").ConfigureAwait(false);

            Test.AreEqual(BigWei, invoice.AmountExpected, "the raw token text is preserved, never a double");
        }).ConfigureAwait(false);

        await Test.RunAsync("an outgoing amount is serialised as a JSON string", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(201, InvoiceJson);

            await client
                .CreateInvoiceAsync(new InvoiceCreateRequest { AmountSats = BigWei, Asset = "eth" })
                .ConfigureAwait(false);

            Test.IsTrue(
                transport.Last.Body!.Contains("\"amount_sats\":\"" + BigWei + "\"", StringComparison.Ordinal),
                "amount_sats goes out quoted");
        }).ConfigureAwait(false);

        await Test.RunAsync("add_fee_ontop is omitted when null and sent as a JSON bool when set", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(201, InvoiceJson);
            transport.Enqueue(201, InvoiceJson);

            await client.CreateInvoiceAsync(new InvoiceCreateRequest { AmountSats = "1000" }).ConfigureAwait(false);
            Test.IsTrue(!transport.Last.Body!.Contains("add_fee_ontop", StringComparison.Ordinal), "omitted by default");

            await client.CreateInvoiceAsync(new InvoiceCreateRequest { AmountSats = "1000", AddFeeOntop = false }).ConfigureAwait(false);
            Test.IsTrue(transport.Last.Body!.Contains("\"add_fee_ontop\":false", StringComparison.Ordinal), "false is sent explicitly");
        }).ConfigureAwait(false);

        await Test.RunAsync("the checkout and fee fields decode", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""
                {"id":"x","address":"bc1q","status":"pending","amount_expected":"101010","amount_received":"0",
                 "decimals":8,"amount_decimal":"0.00101010","payment_uri":"bitcoin:bc1q?amount=0.00101010",
                 "add_fee_ontop":true,"amount_net_requested":"100000","fee_ontop":"1010"}
                """);

            Invoice invoice = await client.GetInvoiceAsync("x").ConfigureAwait(false);

            Test.AreEqual(8, invoice.Decimals ?? -1, "decimals");
            Test.AreEqual("0.00101010", invoice.AmountDecimal, "amount_decimal stays a string");
            Test.IsTrue(invoice.AddFeeOntop, "add_fee_ontop");
            Test.AreEqual("100000", invoice.AmountNetRequested, "amount_net_requested");
            Test.AreEqual("1010", invoice.FeeOntop, "fee_ontop");
        }).ConfigureAwait(false);

        Test.Section("The list envelope and auto-pagination");

        await Test.RunAsync("a list decodes into { data, has_more, next_cursor }", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""{"data":[""" + ForwardJson + """],"has_more":true,"next_cursor":"cursor-2"}""");

            ReceivifyList<Forward> page = await client.ListAddressesAsync().ConfigureAwait(false);

            Test.AreEqual(1, page.Data.Count, "one item on the page");
            Test.IsTrue(page.HasMore, "has_more is a fact from the server");
            Test.AreEqual("cursor-2", page.NextCursor, "next_cursor is carried through");
        }).ConfigureAwait(false);

        await Test.RunAsync("the paginator follows next_cursor until has_more is false", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""{"data":[{"id":"a"},{"id":"b"}],"has_more":true,"next_cursor":"c2"}""");
            transport.EnqueueOk("""{"data":[{"id":"c"}],"has_more":true,"next_cursor":"c3"}""");
            transport.EnqueueOk("""{"data":[{"id":"d"}],"has_more":false,"next_cursor":null}""");

            List<Invoice> invoices = await ReceivifyClient
                .CollectAsync(client.EnumerateInvoicesAsync())
                .ConfigureAwait(false);

            Test.AreEqual(4, invoices.Count, "every item across three pages");
            Test.AreEqual("d", invoices[3].Id, "in order");
            Test.AreEqual(3, transport.Requests.Count, "three requests, no more");
            Test.IsTrue(
                transport.Requests[1].Uri.ToString().Contains("cursor=c2", StringComparison.Ordinal),
                "page two asked for cursor c2");
            Test.IsTrue(
                transport.Requests[2].Uri.ToString().Contains("cursor=c3", StringComparison.Ordinal),
                "page three asked for cursor c3");
        }).ConfigureAwait(false);

        await Test.RunAsync("the paginator refuses to loop on a repeated cursor", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""{"data":[{"id":"a"}],"has_more":true,"next_cursor":"same"}""");
            transport.EnqueueOk("""{"data":[{"id":"b"}],"has_more":true,"next_cursor":"same"}""");

            ReceivifyException error = await Test.ThrowsAsync<ReceivifyException>(
                () => ReceivifyClient.CollectAsync(client.EnumerateInvoicesAsync()),
                "a repeated cursor must throw").ConfigureAwait(false);

            Test.IsTrue(error.Message.Contains("repeated", StringComparison.Ordinal), "and say why");
        }).ConfigureAwait(false);

        await Test.RunAsync("has_more true with no cursor throws rather than spinning", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""{"data":[{"id":"a"}],"has_more":true,"next_cursor":null}""");

            await Test.ThrowsAsync<ReceivifyException>(
                () => ReceivifyClient.CollectAsync(client.EnumerateInvoicesAsync()),
                "an empty next_cursor must throw").ConfigureAwait(false);
        }).ConfigureAwait(false);

        Test.Section("Errors map to typed exceptions");

        await Test.RunAsync("a known code becomes its dedicated exception", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(400, """{"error":"bad_destination","detail":"That is not a BTC address."}""");

            BadDestinationException error = await Test.ThrowsAsync<BadDestinationException>(
                () => client.CreateAddressAsync(new ForwardCreateRequest { Ticker = "btc", Destination = "nope" }),
                "bad_destination maps to BadDestinationException").ConfigureAwait(false);

            Test.AreEqual("bad_destination", error.Code, "the raw code is preserved");
            Test.AreEqual("That is not a BTC address.", error.Detail, "detail is preserved");
            Test.AreEqual(400, error.StatusCode, "status is preserved");
            Test.IsFalse(error.IsRetryable, "a 400 is not retryable");
            Test.IsTrue(
                typeof(ReceivifyApiException).IsInstanceOfType(error),
                "and it is still catchable as the base type");
        }).ConfigureAwait(false);

        await Test.RunAsync("an UNKNOWN code degrades to ReceivifyApiException with the code intact", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(400, """{"error":"teapot_unavailable","detail":"From a future deployment."}""");

            ReceivifyApiException error = await Test.ThrowsAsync<ReceivifyApiException>(
                () => client.GetInvoiceAsync("abc"),
                "an unknown code must not crash an older SDK").ConfigureAwait(false);

            Test.AreEqual(typeof(ReceivifyApiException), error.GetType(), "exactly the base type");
            Test.AreEqual("teapot_unavailable", error.Code, "the unrecognised code is still readable");
        }).ConfigureAwait(false);

        await Test.RunAsync("a 429 carries the rate-limit headers", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create(o => o.MaxRetries = 0);
            transport.Enqueue(
                429,
                """{"error":"rate_limited","detail":"Slow down."}""",
                new Dictionary<string, string>
                {
                    ["Retry-After"] = "7",
                    ["X-RateLimit-Limit"] = "60",
                    ["X-RateLimit-Remaining"] = "0",
                    ["X-RateLimit-Reset"] = "1800000000",
                });

            RateLimitedException error = await Test.ThrowsAsync<RateLimitedException>(
                () => client.GetInvoiceAsync("abc"),
                "429 maps to RateLimitedException").ConfigureAwait(false);

            Test.AreEqual(7, error.RetryAfterSeconds, "Retry-After is parsed");
            Test.AreEqual(60, error.RateLimitLimit, "X-RateLimit-Limit is parsed");
            Test.AreEqual(0, error.RateLimitRemaining, "X-RateLimit-Remaining is parsed");
            Test.AreEqual(1800000000L, error.RateLimitReset, "X-RateLimit-Reset is parsed");
            Test.IsTrue(error.IsRetryable, "rate_limited is retryable");
        }).ConfigureAwait(false);

        await Test.RunAsync("a non-conforming error body still throws with the status", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create(o => o.MaxRetries = 0);
            transport.Enqueue(500, "<html>gateway exploded</html>");

            ReceivifyApiException error = await Test.ThrowsAsync<ReceivifyApiException>(
                () => client.GetInvoiceAsync("abc"),
                "an HTML error page must not crash the parser").ConfigureAwait(false);

            Test.IsNull(error.Code, "no code could be read");
            Test.AreEqual(500, error.StatusCode, "but the status is there");
            Test.IsTrue(error.IsRetryable, "and a 5xx is retryable");
        }).ConfigureAwait(false);

        Test.Section("Retry safety");

        await Test.RunAsync("a GET is retried on 500 and then succeeds", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(500, """{"error":"internal","detail":"Unexpected error."}""");
            transport.EnqueueOk(InvoiceJson);

            Invoice invoice = await client.GetInvoiceAsync("abc").ConfigureAwait(false);

            Test.AreEqual(2, transport.Requests.Count, "one retry");
            Test.AreEqual("order-42", invoice.Reference, "the second attempt's body is returned");
        }).ConfigureAwait(false);

        await Test.RunAsync("a POST WITHOUT an idempotency key is never retried", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(500, """{"error":"internal","detail":"Unexpected error."}""");
            transport.EnqueueOk(InvoiceJson);

            await Test.ThrowsAsync<InternalServerException>(
                () => client.CreateInvoiceAsync(new InvoiceCreateRequest { AmountSats = "1000" }),
                "blind-retrying a create is how you double-charge someone").ConfigureAwait(false);

            Test.AreEqual(1, transport.Requests.Count, "exactly one attempt");
        }).ConfigureAwait(false);

        await Test.RunAsync("a POST WITH an idempotency key on a supporting endpoint is retried", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(500, """{"error":"internal","detail":"Unexpected error."}""");
            transport.Enqueue(201, InvoiceJson);

            await client.CreateInvoiceAsync(
                new InvoiceCreateRequest { AmountSats = "1000" },
                new RequestOptions { IdempotencyKey = "retry-me" }).ConfigureAwait(false);

            Test.AreEqual(2, transport.Requests.Count, "the key made the retry safe");
        }).ConfigureAwait(false);

        await Test.RunAsync("a key on an endpoint that does NOT declare support does not unlock retries", async () =>
        {
            // PATCH /api/v1/address explicitly does not accept Idempotency-Key, so the server would
            // not deduplicate a replay. Sending the header is harmless; retrying on it is not.
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(500, """{"error":"internal","detail":"Unexpected error."}""");
            transport.EnqueueOk(ForwardJson);

            await Test.ThrowsAsync<InternalServerException>(
                () => client.UpdateAddressAsync(
                    "11111111-2222-4333-8444-555555555555",
                    new ForwardUpdateRequest { Status = ForwardStatus.Paused },
                    new RequestOptions { IdempotencyKey = "not-honoured" }),
                "an unsupported endpoint must not be retried").ConfigureAwait(false);

            Test.AreEqual(1, transport.Requests.Count, "exactly one attempt");
        }).ConfigureAwait(false);

        Test.Section("Forwards");

        await Test.RunAsync("Id is canonical and forward_id carries the same value", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(201, ForwardJson);

            ReceivifyResponse<Forward> response = await client.CreateAddressWithStatusAsync(
                new ForwardCreateRequest
                {
                    Ticker = "btc",
                    Destination = "bc1qdestination",
                    Callback = "https://merchant.test/hook",
                }).ConfigureAwait(false);

            Forward forward = response.Value!;
            Test.AreEqual("11111111-2222-4333-8444-555555555555", forward.Id, "Id is the identifier to read");
#pragma warning disable CS0618 // the deprecated twin is exactly what this test is about
            Test.AreEqual(forward.Id, forward.ForwardId, "forward_id is the deprecated twin, same value");
#pragma warning restore CS0618
            Test.AreEqual(201, response.StatusCode, "201 means a new address was derived");
            Test.AreEqual("0.00050000", forward.MinForward, "min_forward is a DISPLAY-unit decimal, not base units");
        }).ConfigureAwait(false);

        await Test.RunAsync("a 200 on create means an existing forward came back", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(200, ForwardJson);

            ReceivifyResponse<Forward> response = await client.CreateAddressWithStatusAsync(
                new ForwardCreateRequest { Ticker = "btc", Destination = "bc1qdestination" }).ConfigureAwait(false);

            Test.AreEqual(200, response.StatusCode, "deterministic creation returned the existing forward");
            Test.IsFalse(response.IdempotentReplay, "and this one was not an idempotency replay");
        }).ConfigureAwait(false);

        await Test.RunAsync("an update sends only the properties that were assigned", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk(ForwardJson);

            await client.UpdateAddressAsync(
                "11111111-2222-4333-8444-555555555555",
                new ForwardUpdateRequest { Status = ForwardStatus.Paused }).ConfigureAwait(false);

            string body = transport.Last.Body!;
            Test.IsTrue(body.Contains("\"status\":\"paused\"", StringComparison.Ordinal), "status is sent");
            Test.IsFalse(body.Contains("callback_url", StringComparison.Ordinal), "an untouched field is omitted");
        }).ConfigureAwait(false);

        await Test.RunAsync("assigning null to a clearable field sends an explicit null", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk(ForwardJson);

            await client.UpdateAddressAsync(
                "11111111-2222-4333-8444-555555555555",
                new ForwardUpdateRequest { CallbackUrl = null }).ConfigureAwait(false);

            Test.IsTrue(
                transport.Last.Body!.Contains("\"callback_url\":null", StringComparison.Ordinal),
                "null CLEARS the callback, so it must be on the wire");
        }).ConfigureAwait(false);

        await Test.RunAsync("a payment link can be converted to open-amount with an explicit null", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""{"id":"abc12345","title":null,"amount_sats":null,"open_amount":true,"active":true,"uses":0,"created_at":null,"updated_at":null,"url":"https://receivify.test/l/abc12345"}""");

            PaymentLink link = await client
                .UpdatePaymentLinkAsync("abc12345", new PaymentLinkUpdateRequest { AmountBtc = null })
                .ConfigureAwait(false);

            Test.IsTrue(
                transport.Last.Body!.Contains("\"amount_btc\":null", StringComparison.Ordinal),
                "the explicit null is what makes the link open-amount");
            Test.IsTrue(link.OpenAmount, "and the response says so");
            Test.IsNull(link.AmountSats, "with no base-unit amount");
        }).ConfigureAwait(false);

        await Test.RunAsync("ForwardDetail spreads the Forward fields and adds the capped logs", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""
                {
                  "id": "11111111-2222-4333-8444-555555555555",
                  "forward_id": "11111111-2222-4333-8444-555555555555",
                  "ticker": "btc",
                  "provider": "bitcoind",
                  "chain": null,
                  "address": "bc1qdeposit",
                  "destination": "bc1qdestination",
                  "callback_url": null,
                  "confirmations_required": 2,
                  "min_forward": "0.00050000",
                  "status": "active",
                  "expires_at": null,
                  "created_at": "2026-08-25T11:00:00.000Z",
                  "updated_at": null,
                  "payments": [
                    { "txid_in": "aa", "value": "250000", "status": "sent", "txid_out": "bb", "confirmations": 3, "created_at": "2026-08-25T11:00:00.000Z" }
                  ],
                  "payments_has_more": true,
                  "callbacks": [
                    { "event": "forward.sent", "url": "https://merchant.test/hook", "status": "delivered", "attempts": 1, "last_status": 200, "last_error": null, "created_at": "2026-08-25T11:00:00.000Z", "delivered_at": "2026-08-25T11:00:01.000Z" }
                  ],
                  "callbacks_has_more": false
                }
                """);

            ForwardDetail detail = await client
                .GetAddressAsync("11111111-2222-4333-8444-555555555555")
                .ConfigureAwait(false);

            Test.AreEqual("bc1qdeposit", detail.Address, "the inherited Forward fields decode");
            Test.AreEqual("active", detail.Status, "including status");
            Test.AreEqual(1, detail.Payments.Count, "the payments log decodes");
            Test.AreEqual("250000", detail.Payments[0].Value, "with money still an exact string");
            Test.IsTrue(detail.PaymentsHasMore, "the 200 cap truncated the view, and says so");
            Test.AreEqual(1, detail.Callbacks.Count, "the callbacks log decodes");
            Test.IsFalse(detail.CallbacksHasMore, "and was not truncated");
        }).ConfigureAwait(false);

        Test.Section("Bare arrays are not list envelopes");

        await Test.RunAsync("PayoutState.Pending is an unbounded bare array", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""
                {
                  "payout_addresses": { "btc": "bc1qlive" },
                  "pending": [
                    { "id": "chg_1", "asset": "btc", "new_address": "bc1qnew", "old_address": "bc1qlive", "status": "confirmed", "requested_at": "2026-08-25T11:00:00.000Z", "effective_at": "2026-08-26T11:00:00.000Z", "confirmed_at": "2026-08-25T11:05:00.000Z", "applied_at": null }
                  ],
                  "timelock_hours": 24,
                  "email_confirmation": true,
                  "instant": false
                }
                """);

            PayoutState state = await client.GetPayoutsAsync().ConfigureAwait(false);

            Test.AreEqual("bc1qlive", state.PayoutAddresses!["btc"], "the LIVE map is what the sweeper reads");
            Test.AreEqual(1, state.Pending.Count, "and staged changes are separate from it");
            Test.AreEqual("bc1qnew", state.Pending[0].NewAddress, "the staged destination");
            Test.IsFalse(state.Instant, "a timelocked deployment applies nothing inline");
        }).ConfigureAwait(false);

        await Test.RunAsync("Summary keeps base units exact and FX rates as advisory doubles", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""
                {
                  "merchant": { "id": "m1", "email": "a@b.test", "payout_addresses": {}, "fee_bps": 50, "api_key_prefix": "rcv_live_ab", "display_currency": "USD" },
                  "counts": { "total": 3, "paid": 2 },
                  "by_asset": [
                    { "symbol": "eth", "name": "Ether", "color": "#627eea", "decimals": 18, "count": 2, "paid_count": 2, "volume_base": "1234567890123456789", "fee_base": "6172839450617283", "net_base": "1228395050672839506" }
                  ],
                  "by_status": { "paid": 2 },
                  "series": [ { "day": "2026-08-25", "assets": { "eth": "1234567890123456789" } } ],
                  "fx": { "base": "USD", "crypto": { "eth": 3412.55 }, "fiat": { "EUR": 0.92 }, "fetched_at": null, "source": "test", "stale": false },
                  "currencies": [ "USD", "EUR" ]
                }
                """);

            Summary summary = await client.GetSummaryAsync().ConfigureAwait(false);

            Test.AreEqual(50, summary.Merchant!.FeeBps, "fee_bps is an integer rate, not money");
            Test.AreEqual(
                "1234567890123456789",
                summary.ByAsset[0].VolumeBase,
                "volume stays an exact base-unit string");
            Test.AreEqual(
                "1234567890123456789",
                summary.Series[0].Assets!["eth"],
                "and so does a series entry");
            Test.AreEqual(3412.55, summary.Fx!.Crypto!["eth"], "an FX rate is deliberately a double");
            Test.IsFalse(summary.Fx.Stale, "and it is fresh");
        }).ConfigureAwait(false);

        Test.Section("Shown-once fields and idempotent replays");

        await Test.RunAsync("a replayed key creation has a null secret and the replay header", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(
                201,
                """{"id":"key_1","api_key":null,"api_key_prefix":"rcv_live_ab","prefix":"rcv_live_ab","label":"ci","created_at":null,"message":"Replayed."}""",
                new Dictionary<string, string> { ["Idempotent-Replay"] = "true" });

            ReceivifyResponse<ApiKeyCreated> response = await client.SendAsync<ApiKeyCreated>(
                HttpMethod.Post,
                "/api/v1/keys",
                query: null,
                new ApiKeyCreateRequest { Label = "ci" },
                new RequestOptions { IdempotencyKey = "same-key" },
                requiresAuth: true,
                supportsIdempotencyKey: true).ConfigureAwait(false);

            Test.IsTrue(response.IdempotentReplay, "Idempotent-Replay: true is surfaced");
            Test.IsNull(response.Value!.ApiKeyValue, "the secret is never stored for a replay");
            Test.AreEqual("key_1", response.Value.Id, "but the id still identifies what was created");
        }).ConfigureAwait(false);

        Test.Section("Health does not conform to the error contract");

        await Test.RunAsync("a 503 health report is returned, not thrown", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.Enqueue(
                503,
                """{"status":"degraded","time":"2026-08-25T11:00:00.000Z","checks":{"mongo":{"ok":false,"error":"down"}},"alerts":[]}""");

            HealthReport report = await client.GetHealthAsync().ConfigureAwait(false);

            Test.AreEqual("degraded", report.Status, "the report comes back on a 503");
            Test.IsTrue(report.IsDegraded, "and says a hard dependency is down");
            Test.AreEqual(
                JsonValueKind.Object,
                report.Checks.ValueKind,
                "checks stays raw JSON — it is an operator diagnostic, not a contract");
        }).ConfigureAwait(false);

        Test.Section("Escape hatches");

        await Test.RunAsync("RawBody exposes a field the SDK does not model", async () =>
        {
            (ReceivifyClient client, FakeTransport transport) = TestClient.Create();
            transport.EnqueueOk("""{"id":"x","status":"paid","address":"a","amount_expected":"1","amount_received":"1","expires_at":null,"future_field":"kept"}""");

            ReceivifyResponse<PublicInvoice> response = await client
                .SendAsync<PublicInvoice>(HttpMethod.Get, "/api/public/invoices/x", requiresAuth: false)
                .ConfigureAwait(false);

            Test.AreEqual("paid", response.Value!.Status, "the modelled fields decode");
            Test.AreEqual(
                "kept",
                response.AsJson()!.Value.GetProperty("future_field").GetString(),
                "and an unmodelled one is still reachable");
        }).ConfigureAwait(false);
    }
}
