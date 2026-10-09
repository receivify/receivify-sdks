using System.Text;

namespace Receivify.Tests;

internal static class WebhookTests
{
    private const string CurrentSecret = "whsec_current_aaaaaaaaaaaaaaaaaaaa";
    private const string PreviousSecret = "whsec_previous_bbbbbbbbbbbbbbbbbbb";
    private const string WrongSecret = "whsec_attacker_cccccccccccccccccccc";

    private const long SignedAt = 1_800_000_000;

    private const string InvoiceBody = """
        {"event":"invoice.paid","sequence":9412,"created_at":"2026-08-25T11:00:00.000Z","data":{"id":"6f1b0f6a-0000-4000-8000-000000000001","reference":"order-42","status":"paid","amount_expected":"1234567890123456789","amount_received":"1234567890123456789","address":"0xdeadbeef","paid_at":"2026-08-25T11:00:00.000Z"}}
        """;

    private const string ForwardBody = """
        {"event":"forward.sent","sequence":9413,"created_at":"2026-08-25T11:05:00.000Z","data":{"forward_id":"11111111-2222-4333-8444-555555555555","ticker":"btc","address_in":"bc1qdeposit","address_out":"bc1qdestination","txid_in":"aa","txid_out":"bb","value":"250000","value_forwarded":"249000","fee":"1000","confirmations":3,"pending":false}}
        """;

    private static WebhookVerifyOptions AtSigningTime => new() { Now = DateTimeOffset.FromUnixTimeSeconds(SignedAt) };

    public static async Task RunAsync()
    {
        Test.Section("Webhook signature verification");

        await Test.RunAsync("a delivery signed with the CURRENT secret verifies", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            Test.IsTrue(
                ReceivifyWebhooks.Verify(body, header, new[] { CurrentSecret, PreviousSecret }, AtSigningTime),
                "the current secret is tried");
        }).ConfigureAwait(false);

        await Test.RunAsync("a delivery signed with the PREVIOUS secret still verifies during the grace window", () =>
        {
            // This is the whole reason the signature takes a list. Receivify signs with the current
            // secret only, but a delivery enqueued before a rotation can still be in flight, and a
            // receiver that has already deployed the new secret must accept the old one until the
            // window closes.
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, PreviousSecret, SignedAt);

            Test.IsTrue(
                ReceivifyWebhooks.Verify(body, header, new[] { CurrentSecret, PreviousSecret }, AtSigningTime),
                "the previous secret is tried too");
            Test.IsFalse(
                ReceivifyWebhooks.Verify(body, header, new[] { CurrentSecret }, AtSigningTime),
                "and dropping it after the window closes correctly starts rejecting");
        }).ConfigureAwait(false);

        await Test.RunAsync("a signature from an unknown secret is rejected", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, WrongSecret, SignedAt);

            WebhookVerificationResult result = ReceivifyWebhooks.Describe(
                body,
                header,
                new[] { CurrentSecret, PreviousSecret },
                AtSigningTime);

            Test.IsFalse(result.Ok, "rejected");
            Test.AreEqual(WebhookFailureReason.NoMatch, result.Reason, "with a reason worth logging");
        }).ConfigureAwait(false);

        await Test.RunAsync("a TAMPERED body fails even with a genuine signature", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            byte[] tampered = Encoding.UTF8.GetBytes(
                InvoiceBody.Replace("\"amount_received\":\"1234567890123456789\"", "\"amount_received\":\"1\"", StringComparison.Ordinal));

            Test.IsFalse(
                ReceivifyWebhooks.Verify(tampered, header, new[] { CurrentSecret }, AtSigningTime),
                "one changed digit breaks the HMAC");
        }).ConfigureAwait(false);

        await Test.RunAsync("a STALE timestamp fails the tolerance window", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            var muchLater = new WebhookVerifyOptions
            {
                Now = DateTimeOffset.FromUnixTimeSeconds(SignedAt + 301),
            };

            WebhookVerificationResult result =
                ReceivifyWebhooks.Describe(body, header, new[] { CurrentSecret }, muchLater);

            Test.IsFalse(result.Ok, "outside the 300s default window");
            Test.AreEqual(WebhookFailureReason.StaleTimestamp, result.Reason, "and it says why");

            var justInside = new WebhookVerifyOptions
            {
                Now = DateTimeOffset.FromUnixTimeSeconds(SignedAt + 299),
            };

            Test.IsTrue(
                ReceivifyWebhooks.Verify(body, header, new[] { CurrentSecret }, justInside),
                "299 seconds later is still fine");
        }).ConfigureAwait(false);

        await Test.RunAsync("a re-serialised body does not verify", () =>
        {
            // The point of the raw-bytes rule, made concrete: the same JSON with different
            // whitespace is different bytes, and the HMAC covers bytes.
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            string reserialised = InvoiceBody.Replace("\":", "\": ", StringComparison.Ordinal);

            Test.IsFalse(
                ReceivifyWebhooks.Verify(reserialised, header, new[] { CurrentSecret }, AtSigningTime),
                "never hand verifyWebhook a re-serialised object");
        }).ConfigureAwait(false);

        await Test.RunAsync("a malformed or missing header is reported, not thrown", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);

            Test.AreEqual(
                WebhookFailureReason.BadHeader,
                ReceivifyWebhooks.Describe(body, null, new[] { CurrentSecret }).Reason,
                "a missing header");
            Test.AreEqual(
                WebhookFailureReason.BadHeader,
                ReceivifyWebhooks.Describe(body, "garbage", new[] { CurrentSecret }).Reason,
                "a header that is not t=,v1=");
            Test.AreEqual(
                WebhookFailureReason.BadHeader,
                ReceivifyWebhooks.Describe(body, "t=notanumber,v1=abcd", new[] { CurrentSecret }).Reason,
                "a non-numeric timestamp");
            Test.AreEqual(
                WebhookFailureReason.NoMatch,
                ReceivifyWebhooks.Describe(body, "t=" + SignedAt + ",v1=zzzz", new[] { CurrentSecret }, AtSigningTime).Reason,
                "a non-hex signature is simply no match");
        }).ConfigureAwait(false);

        await Test.RunAsync("no configured secret is its own reason", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            Test.AreEqual(
                WebhookFailureReason.NoSecrets,
                ReceivifyWebhooks.Describe(body, header, Array.Empty<string>()).Reason,
                "nothing to check against");
        }).ConfigureAwait(false);

        await Test.RunAsync("the client verifies against its configured secret list", () =>
        {
            (ReceivifyClient client, FakeTransport _) = TestClient.Create(o =>
                o.WebhookSecrets = new[] { CurrentSecret, PreviousSecret });

            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);

            Test.AreEqual(2, client.WebhookSecrets.Count, "both secrets are held");
            Test.IsTrue(
                client.VerifyWebhook(body, ReceivifyWebhooks.Sign(body, PreviousSecret, SignedAt), null, AtSigningTime),
                "and either one verifies");
        }).ConfigureAwait(false);

        await Test.RunAsync("a single secret is normalised to a list", () =>
        {
            (ReceivifyClient client, FakeTransport _) = TestClient.Create(o => o.WebhookSecret = CurrentSecret);

            Test.AreEqual(1, client.WebhookSecrets.Count, "one secret, still a list");
            Test.AreEqual(CurrentSecret, client.WebhookSecrets[0], "and it is the right one");
        }).ConfigureAwait(false);

        Test.Section("Webhook envelope");

        await Test.RunAsync("the envelope is never flattened and invoice data types out", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            WebhookEvent evt = ReceivifyWebhooks.Parse(body, header, new[] { CurrentSecret }, AtSigningTime);

            Test.AreEqual(EventNames.InvoicePaid, evt.Event, "the discriminator");
            Test.AreEqual(9412L, evt.Sequence, "the dedupe counter");
            Test.IsNotNull(evt.CreatedAt, "when the event happened");
            Test.IsTrue(evt.IsInvoiceEvent, "invoice-shaped");
            Test.IsFalse(evt.IsForwardEvent, "not forward-shaped");

            InvoiceEventData data = evt.AsInvoiceData()!;
            Test.AreEqual("order-42", data.Reference, "data decodes");
            Test.AreEqual("1234567890123456789", data.AmountReceived, "and money is still an exact string");
            Test.IsNull(evt.AsForwardData(), "the wrong accessor returns null rather than nonsense");
        }).ConfigureAwait(false);

        await Test.RunAsync("a forward event decodes into the CryptAPI-shaped payload", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(ForwardBody);
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            WebhookEvent evt = ReceivifyWebhooks.Parse(body, header, new[] { CurrentSecret }, AtSigningTime);
            ForwardEventData data = evt.AsForwardData()!;

            Test.IsTrue(evt.IsForwardEvent, "forward-shaped");
            Test.AreEqual("bc1qdeposit", data.AddressIn, "address_in is the deposit address");
            Test.AreEqual("bc1qdestination", data.AddressOut, "address_out is the destination");
            Test.AreEqual("249000", data.ValueForwarded, "net forwarded, base units as a string");
            Test.AreEqual("1000", data.Fee, "fee, base units as a string");
        }).ConfigureAwait(false);

        await Test.RunAsync("sweep.sent and test are invoice-shaped, per the frozen event set", () =>
        {
            Test.IsTrue(EventNames.IsInvoiceEvent(EventNames.SweepSent), "sweep.sent carries invoice data");
            Test.IsTrue(EventNames.IsInvoiceEvent(EventNames.Test), "so does test");
            Test.IsFalse(EventNames.IsInvoiceEvent(EventNames.ForwardConfirmed), "forward.* does not");
            Test.AreEqual(16, EventNames.All.Count, "the frozen set has sixteen names");
        }).ConfigureAwait(false);

        await Test.RunAsync("an unknown future event name does not crash the parser", () =>
        {
            string futureBody =
                """{"event":"invoice.escrowed","sequence":1,"created_at":"2026-08-25T11:00:00.000Z","data":{"id":"x","reference":null,"status":"escrowed","amount_expected":"1","amount_received":"1","address":"a","paid_at":null}}""";

            byte[] body = Encoding.UTF8.GetBytes(futureBody);
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            WebhookEvent evt = ReceivifyWebhooks.Parse(body, header, new[] { CurrentSecret }, AtSigningTime);

            Test.AreEqual("invoice.escrowed", evt.Event, "the unknown name is preserved verbatim");
            Test.IsTrue(evt.IsInvoiceEvent, "and prefix matching still classifies it");
        }).ConfigureAwait(false);

        await Test.RunAsync("Parse throws with a reason on a bad signature", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes(InvoiceBody);
            string header = ReceivifyWebhooks.Sign(body, WrongSecret, SignedAt);

            ReceivifyWebhookException error = Test.Throws<ReceivifyWebhookException>(
                () => ReceivifyWebhooks.Parse(body, header, new[] { CurrentSecret }, AtSigningTime),
                "Parse must not return an unverified event");

            Test.AreEqual(WebhookFailureReason.NoMatch, error.Reason, "with a machine-readable reason");
        }).ConfigureAwait(false);

        await Test.RunAsync("Parse throws on a verified body that is not an envelope", () =>
        {
            byte[] body = Encoding.UTF8.GetBytes("""{"not":"an envelope"}""");
            string header = ReceivifyWebhooks.Sign(body, CurrentSecret, SignedAt);

            ReceivifyWebhookException error = Test.Throws<ReceivifyWebhookException>(
                () => ReceivifyWebhooks.Parse(body, header, new[] { CurrentSecret }, AtSigningTime),
                "a signed non-envelope is still a failure");

            Test.AreEqual(WebhookFailureReason.BadEnvelope, error.Reason, "reported as a bad envelope");
        }).ConfigureAwait(false);

    }
}
