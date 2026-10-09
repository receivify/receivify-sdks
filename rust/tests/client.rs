mod common;

use std::time::Duration;

use common::*;
use receivify::{
    AddressListParams, Client, CreateAddress, CreateInvoice, Error, ErrorCode, ForwardStatus,
    InvoiceStatus, ListParams, Method, RequestOptions, TransportError, UpdateAddress,
};

// ---------------------------------------------------------------- configuration

#[test]
fn builder_rejects_an_empty_or_schemeless_base_url() {
    let fake = Fake::new();
    let err = Client::builder("  ")
        .transport(fake.clone())
        .build()
        .unwrap_err();
    assert!(matches!(err, Error::Config(_)));
    let err = Client::builder("receivify.io")
        .transport(fake)
        .build()
        .unwrap_err();
    assert!(matches!(err, Error::Config(_)));
}

#[test]
fn builder_rejects_an_api_key_with_embedded_whitespace() {
    let err = Client::builder(BASE)
        .api_key("rcv_live_abc\r\nx-evil: 1")
        .transport(Fake::new())
        .build()
        .unwrap_err();
    assert!(matches!(err, Error::Config(_)));
}

#[test]
fn trailing_slashes_are_trimmed() {
    let fake = Fake::new();
    fake.reply(200, &invoice_json("abc"));
    let client = Client::builder(format!("{BASE}///"))
        .transport(fake.clone())
        .build()
        .unwrap();
    client.get_invoice("abc").unwrap();
    assert_eq!(fake.last().url, format!("{BASE}/api/v1/invoices/abc"));
}

#[test]
fn debug_output_never_contains_the_api_key_or_secrets() {
    let fake = Fake::new();
    let client = fake
        .builder()
        .webhook_secrets(["whsec_topsecret"])
        .build()
        .unwrap();
    let dbg = format!("{client:?}");
    assert!(!dbg.contains(KEY), "{dbg}");
    assert!(!dbg.contains("whsec_topsecret"), "{dbg}");
    assert!(dbg.contains("[redacted]"));
    let builder_dbg = format!("{:?}", fake.builder());
    assert!(!builder_dbg.contains(KEY));
}

// ---------------------------------------------------------------- request building

#[test]
fn an_authenticated_get_carries_the_documented_headers() {
    let fake = Fake::new();
    fake.reply(200, &invoice_json("inv-1"));
    fake.client().get_invoice("inv-1").unwrap();
    let req = fake.last();
    assert_eq!(req.method, Method::Get);
    assert_eq!(
        req.header("authorization"),
        Some(format!("Bearer {KEY}").as_str())
    );
    assert_eq!(req.header("accept"), Some("application/json"));
    assert_eq!(
        req.header("user-agent"),
        Some(format!("receivify-rust/{}", receivify::VERSION).as_str())
    );
    assert_eq!(req.timeout, Duration::from_secs(30));
    assert!(req.body.is_none());
    assert!(req.header("content-type").is_none());
}

#[test]
fn the_api_key_is_never_in_the_url_and_is_masked_in_request_debug() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&[], false, None));
    fake.client()
        .list_invoices(&ListParams::new().limit(5))
        .unwrap();
    let req = fake.last();
    assert!(!req.url.contains(KEY));
    let dbg = format!("{req:?}");
    assert!(!dbg.contains(KEY), "{dbg}");
}

#[test]
fn a_user_agent_suffix_is_appended_not_substituted() {
    let fake = Fake::new();
    fake.reply(200, &invoice_json("x"));
    let client = fake
        .builder()
        .user_agent_suffix("my-shop/2.1")
        .build()
        .unwrap();
    client.get_invoice("x").unwrap();
    assert_eq!(
        fake.last().header("user-agent").unwrap(),
        format!("receivify-rust/{} my-shop/2.1", receivify::VERSION)
    );
}

#[test]
fn the_timeout_is_configurable_globally_and_per_call() {
    let fake = Fake::new();
    fake.reply(201, &invoice_json("a"))
        .reply(201, &invoice_json("b"));
    let client = fake
        .builder()
        .timeout(Duration::from_secs(5))
        .build()
        .unwrap();
    client
        .create_invoice(&CreateInvoice::default(), None)
        .unwrap();
    assert_eq!(fake.last().timeout, Duration::from_secs(5));
    let opts = RequestOptions::default().with_timeout(Duration::from_millis(1500));
    client
        .create_invoice(&CreateInvoice::default(), Some(&opts))
        .unwrap();
    assert_eq!(fake.last().timeout, Duration::from_millis(1500));
}

#[test]
fn path_ids_are_percent_encoded() {
    let fake = Fake::new();
    fake.reply(200, &invoice_json("x"));
    fake.client().get_invoice("../keys?x=1#y").unwrap();
    assert_eq!(
        fake.last().url,
        format!("{BASE}/api/v1/invoices/..%2Fkeys%3Fx%3D1%23y")
    );
}

#[test]
fn an_empty_id_is_rejected_before_any_request() {
    let fake = Fake::new();
    let client = fake.client();
    assert!(matches!(
        client.get_invoice(""),
        Err(Error::InvalidArgument(_))
    ));
    assert!(matches!(
        client.cancel_invoice(" "),
        Err(Error::InvalidArgument(_))
    ));
    assert!(matches!(
        client.get_address(""),
        Err(Error::InvalidArgument(_))
    ));
    assert_eq!(fake.count(), 0);
}

#[test]
fn list_query_parameters_are_built_and_encoded_and_absent_ones_dropped() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&[], false, None))
        .reply(200, r#"{"data":[],"has_more":false,"next_cursor":null}"#)
        .reply(200, &page_json(&[], false, None));
    let client = fake.client();
    client
        .list_invoices(&ListParams::new().limit(2).cursor("a+b/c=").status("paid"))
        .unwrap();
    assert_eq!(
        query_of(&fake.last().url),
        "limit=2&cursor=a%2Bb%2Fc%3D&status=paid"
    );
    client
        .list_addresses(
            &AddressListParams::new()
                .ticker("usdc_base")
                .status("active"),
        )
        .unwrap();
    assert_eq!(path_of(&fake.last().url), "/api/v1/address");
    assert_eq!(query_of(&fake.last().url), "status=active&ticker=usdc_base");
    client.list_invoices(&ListParams::default()).unwrap();
    assert_eq!(fake.last().url, format!("{BASE}/api/v1/invoices"));
}

#[test]
fn create_invoice_posts_json_with_an_idempotency_key() {
    let fake = Fake::new();
    fake.reply(201, &invoice_json("inv-1"));
    let params = CreateInvoice {
        amount_fiat: Some("25.00".into()),
        currency: Some("USD".into()),
        asset: Some("btc".into()),
        reference: Some("order-1".into()),
        ..Default::default()
    };
    let inv = fake
        .client()
        .create_invoice(&params, Some(&RequestOptions::idempotency_key("order-1")))
        .unwrap();
    assert_eq!(inv.id, "inv-1");
    let req = fake.last();
    assert_eq!(req.method, Method::Post);
    assert_eq!(path_of(&req.url), "/api/v1/invoices");
    assert_eq!(req.header("content-type"), Some("application/json"));
    assert_eq!(req.header("idempotency-key"), Some("order-1"));
    assert_eq!(
        fake.last_body_json(),
        serde_json::json!({"amount_fiat":"25.00","currency":"USD","asset":"btc","reference":"order-1"})
    );
}

#[test]
fn no_idempotency_key_header_without_one() {
    let fake = Fake::new();
    fake.reply(201, &invoice_json("i"));
    fake.client()
        .create_invoice(&CreateInvoice::default(), None)
        .unwrap();
    assert!(fake.last().header("idempotency-key").is_none());
    assert_eq!(fake.last_body_json(), serde_json::json!({}));
}

#[test]
fn add_fee_ontop_none_is_omitted_false_and_true_are_real_booleans() {
    let fake = Fake::new();
    for _ in 0..3 {
        fake.reply(201, &invoice_json("i"));
    }
    let client = fake.client();
    let base = CreateInvoice {
        amount: Some("25".into()),
        asset: Some("usdc".into()),
        chain: Some("base".into()),
        ..Default::default()
    };

    client.create_invoice(&base, None).unwrap();
    let body = fake.last_body_json();
    assert!(
        body.get("add_fee_ontop").is_none(),
        "None must be omitted: {body}"
    );
    assert!(!fake.last().body_str().contains("add_fee_ontop"));

    client
        .create_invoice(
            &CreateInvoice {
                add_fee_ontop: Some(false),
                ..base.clone()
            },
            None,
        )
        .unwrap();
    assert_eq!(
        fake.last_body_json()["add_fee_ontop"],
        serde_json::Value::Bool(false)
    );

    client
        .create_invoice(
            &CreateInvoice {
                add_fee_ontop: Some(true),
                ..base
            },
            None,
        )
        .unwrap();
    assert_eq!(
        fake.last_body_json()["add_fee_ontop"],
        serde_json::Value::Bool(true)
    );
}

#[test]
fn the_new_invoice_fields_decode() {
    let fake = Fake::new();
    fake.reply(
        201,
        r#"{"id":"i","reference":null,"asset":"usdc","amount_expected":"25125629","amount_received":"0",
            "amount_fiat":null,"fiat_currency":null,"chain":"base","provider":"evm","address":"0xabc",
            "status":"pending","expires_at":"2026-01-01T01:00:00.000Z","paid_at":null,
            "created_at":"2026-01-01T00:00:00.000Z","metadata":{},"checkout_url":"https://x/pay/i",
            "decimals":6,"amount_decimal":"25.125629","payment_uri":null,"add_fee_ontop":true,
            "amount_net_requested":"25000000","fee_ontop":"125629"}"#,
    );
    let inv = fake
        .client()
        .create_invoice(&CreateInvoice::default(), None)
        .unwrap();
    assert_eq!(inv.decimals, Some(6));
    assert_eq!(inv.amount_decimal.as_deref(), Some("25.125629"));
    assert_eq!(inv.payment_uri, None);
    assert!(inv.add_fee_ontop);
    assert_eq!(inv.amount_net_requested.as_deref(), Some("25000000"));
    assert_eq!(inv.fee_ontop.as_deref(), Some("125629"));
    assert_eq!(inv.chain.as_deref(), Some("base"));
    assert!(inv.extra.is_empty());
}

#[test]
fn an_invoice_from_an_older_server_defaults_the_new_fields() {
    let fake = Fake::new();
    fake.reply(
        200,
        r#"{"id":"i","reference":"r","asset":"btc","amount_expected":"1","amount_received":"0",
            "address":"bc1q","status":"pending","expires_at":"e","created_at":"c","checkout_url":"u"}"#,
    );
    let inv = fake.client().get_invoice("i").unwrap();
    assert!(!inv.add_fee_ontop);
    assert_eq!(inv.decimals, None);
    assert_eq!(inv.payment_uri, None);
    assert_eq!(inv.amount_net_requested, None);
    assert_eq!(inv.fee_ontop, None);
    assert!(inv.metadata.is_empty());
}

#[test]
fn payment_uri_decodes_when_present() {
    let fake = Fake::new();
    fake.reply(200, &invoice_json("i"));
    let inv = fake.client().get_invoice("i").unwrap();
    assert_eq!(
        inv.payment_uri.as_deref(),
        Some("bitcoin:bc1qexample?amount=0.00100000")
    );
    assert_eq!(inv.status, InvoiceStatus::Pending);
}

// ---------------------------------------------------------------- money stays exact

#[test]
fn money_survives_as_an_exact_string_far_beyond_f64_and_u64() {
    let fake = Fake::new();
    let wei = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
    fake.reply(
        200,
        &invoice_json("i")
            .replace(
                r#""amount_expected":"100000""#,
                &format!(r#""amount_expected":"{wei}""#),
            )
            .replace(r#""amount_fiat":null"#, r#""amount_fiat":"25.10""#),
    );
    let inv = fake.client().get_invoice("i").unwrap();
    assert_eq!(inv.amount_expected, wei);
    assert_eq!(inv.amount_fiat.as_deref(), Some("25.10"));
}

#[test]
fn a_money_field_sent_as_a_json_number_keeps_its_exact_digits() {
    let fake = Fake::new();
    fake.reply(
        200,
        &invoice_json("i")
            .replace(
                r#""amount_expected":"100000""#,
                r#""amount_expected":2100000000000000000000001"#,
            )
            .replace(
                r#""amount_received":"0""#,
                r#""amount_received":9007199254740993"#,
            )
            .replace(r#""amount_fiat":null"#, r#""amount_fiat":25.10"#),
    );
    let inv = fake.client().get_invoice("i").unwrap();
    assert_eq!(inv.amount_expected, "2100000000000000000000001");
    assert_eq!(inv.amount_received, "9007199254740993");
    assert_eq!(inv.amount_fiat.as_deref(), Some("25.10"));
}

#[test]
fn unknown_fields_and_unknown_statuses_are_kept() {
    let fake = Fake::new();
    fake.reply(
        200,
        &invoice_json("i")
            .replace(r#""status":"pending""#, r#""status":"on_hold""#)
            .replace(
                r#""fee_ontop":"0""#,
                r#""fee_ontop":"0","future_field":{"x":1},"risk_score":12345678901234567890123"#,
            ),
    );
    let inv = fake.client().get_invoice("i").unwrap();
    assert_eq!(inv.status, InvoiceStatus::Other("on_hold".into()));
    assert_eq!(inv.status.as_str(), "on_hold");
    assert_eq!(inv.extra["future_field"]["x"], 1);
    assert_eq!(
        inv.extra["risk_score"].to_string(),
        "12345678901234567890123"
    );
}

// ---------------------------------------------------------------- forwarding addresses

#[test]
fn forwarding_address_crud_uses_the_right_verbs_and_bodies() {
    let fake = Fake::new();
    fake.reply(201, &forward_json("f1"))
        .reply(
            200,
            &forward_json("f1").replace(
                r#""updated_at":null}"#,
                r#""updated_at":null,"payments":[{"txid_in":"t","value":"5000","status":"sent","txid_out":"o","confirmations":3,"created_at":"c"}],"payments_has_more":false,"callbacks":[{"event":"forward.sent","url":"https://shop.example/hook","status":"delivered","attempts":1,"last_status":200,"last_error":null,"created_at":"c","delivered_at":"d"}],"callbacks_has_more":true}"#,
            ),
        )
        .reply(200, &forward_json("f1").replace(r#""status":"active""#, r#""status":"paused""#))
        .reply(200, &forward_json("f1").replace(r#""status":"active""#, r#""status":"disabled""#));
    let client = fake.client();

    let fwd = client
        .create_address(
            &CreateAddress {
                ticker: "btc".into(),
                destination: "bc1qdest".into(),
                callback: Some("https://shop.example/hook".into()),
                ..Default::default()
            },
            Some(&RequestOptions::idempotency_key("setup-1")),
        )
        .unwrap();
    assert_eq!(fwd.id, "f1");
    assert_eq!(fwd.forward_id.as_deref(), Some("f1"));
    assert_eq!(fwd.min_forward.as_deref(), Some("0.00010000"));
    assert_eq!(fake.last().method, Method::Post);
    assert_eq!(path_of(&fake.last().url), "/api/v1/address");
    assert_eq!(
        fake.last_body_json(),
        serde_json::json!({"ticker":"btc","destination":"bc1qdest","callback":"https://shop.example/hook"})
    );
    assert_eq!(fake.last().header("idempotency-key"), Some("setup-1"));

    let detail = client.get_address("f1").unwrap();
    assert_eq!(detail.id, "f1"); // Deref to Forward
    assert_eq!(detail.payments[0].value, "5000");
    assert!(detail.callbacks_has_more);
    assert_eq!(detail.callbacks[0].last_status, Some(200));
    assert!(
        detail.forward.extra.is_empty(),
        "{:?}",
        detail.forward.extra
    );

    let paused = client
        .update_address(
            "f1",
            &UpdateAddress {
                status: Some(ForwardStatus::Paused),
                ..Default::default()
            },
        )
        .unwrap();
    assert_eq!(paused.status, ForwardStatus::Paused);
    assert_eq!(fake.last().method, Method::Patch);
    assert_eq!(
        fake.last_body_json(),
        serde_json::json!({"status":"paused"})
    );

    let disabled = client.disable_address("f1").unwrap();
    assert_eq!(disabled.status, ForwardStatus::Disabled);
    assert_eq!(fake.last().method, Method::Delete);
    assert_eq!(path_of(&fake.last().url), "/api/v1/address/f1");
}

#[test]
fn update_address_can_clear_the_callback_with_an_explicit_null() {
    let fake = Fake::new();
    fake.reply(200, &forward_json("f1"));
    fake.client()
        .update_address(
            "f1",
            &UpdateAddress {
                callback_url: Some(None),
                ..Default::default()
            },
        )
        .unwrap();
    assert_eq!(
        fake.last_body_json(),
        serde_json::json!({"callback_url": null})
    );
}

#[test]
fn list_tickers_is_public_and_sends_no_credential() {
    let fake = Fake::new();
    fake.reply(
        200,
        r#"{"data":[{"ticker":"btc","provider":"bitcoind","chain":null,"name":"Bitcoin","decimals":8},{"ticker":"usdc_base","provider":"evm","chain":"base","name":"USD Coin (Base)","decimals":6}],"has_more":false,"next_cursor":null}"#,
    );
    let page = fake.client().list_tickers().unwrap();
    assert_eq!(page.data.len(), 2);
    assert_eq!(page.data[1].decimals, 6);
    assert!(fake.last().header("authorization").is_none());
    assert_eq!(path_of(&fake.last().url), "/api/v1/tickers");
}

#[test]
fn a_client_without_an_api_key_still_calls_public_routes() {
    let fake = Fake::new();
    fake.reply(200, r#"{"data":[],"has_more":false,"next_cursor":null}"#);
    let client = Client::builder(BASE)
        .transport(fake.clone())
        .build()
        .unwrap();
    client.list_tickers().unwrap();
    assert!(fake.last().header("authorization").is_none());
}

// ---------------------------------------------------------------- list envelope + pagination

#[test]
fn a_page_is_the_envelope_untouched() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&["a", "b"], true, Some("c2")));
    let page = fake
        .client()
        .list_invoices(&ListParams::new().limit(2))
        .unwrap();
    assert_eq!(page.data.len(), 2);
    assert!(page.has_more);
    assert_eq!(page.next_cursor.as_deref(), Some("c2"));
}

#[test]
fn auto_pagination_follows_next_cursor_and_keeps_filters() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&["a", "b"], true, Some("c1")))
        .reply(200, &page_json(&["c", "d"], true, Some("c2")))
        .reply(200, &page_json(&["e"], false, None));
    let client = fake.client();
    let ids: Vec<String> = client
        .invoices(ListParams::new().limit(2).status("pending"))
        .map(|r| r.unwrap().id)
        .collect();
    assert_eq!(ids, ["a", "b", "c", "d", "e"]);
    assert_eq!(fake.count(), 3);
    assert_eq!(query_of(&fake.req(0).url), "limit=2&status=pending");
    assert_eq!(
        query_of(&fake.req(1).url),
        "limit=2&cursor=c1&status=pending"
    );
    assert_eq!(
        query_of(&fake.req(2).url),
        "limit=2&cursor=c2&status=pending"
    );
}

#[test]
fn auto_pagination_is_lazy_and_stops_when_you_stop() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&["a", "b"], true, Some("c1")))
        .reply(200, &page_json(&["c", "d"], true, Some("c2")));
    let client = fake.client();
    let first: Vec<_> = client.invoices(ListParams::new()).take(2).collect();
    assert_eq!(first.len(), 2);
    assert_eq!(fake.count(), 1);
}

#[test]
fn auto_pagination_on_a_single_complete_page_makes_one_request() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&["a"], false, None));
    let n = fake.client().invoices(ListParams::new()).count();
    assert_eq!(n, 1);
    assert_eq!(fake.count(), 1);
}

#[test]
fn auto_pagination_refuses_a_repeated_cursor() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&["a"], true, Some("same")))
        .reply(200, &page_json(&["b"], true, Some("same")));
    let results: Vec<_> = fake.client().invoices(ListParams::new()).collect();
    assert_eq!(results.len(), 3);
    assert!(results[0].is_ok() && results[1].is_ok());
    assert!(matches!(results[2], Err(Error::Pagination(_))));
    assert_eq!(fake.count(), 2);
}

#[test]
fn auto_pagination_refuses_has_more_without_a_cursor() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&["a"], true, None));
    let results: Vec<_> = fake.client().invoices(ListParams::new()).collect();
    assert_eq!(results.len(), 2);
    assert!(matches!(results[1], Err(Error::Pagination(_))));
}

#[test]
fn auto_pagination_surfaces_an_api_error_once_and_ends() {
    let fake = Fake::new();
    fake.reply(200, &page_json(&["a"], true, Some("c1"))).reply(
        400,
        r#"{"error":"bad_request","detail":"Malformed cursor."}"#,
    );
    let results: Vec<_> = fake.client().invoices(ListParams::new()).collect();
    assert_eq!(results.len(), 2);
    assert_eq!(
        results[1].as_ref().unwrap_err().code(),
        Some(ErrorCode::BadRequest)
    );
}

#[test]
fn addresses_auto_paginate_too() {
    let fake = Fake::new();
    fake.reply(
        200,
        &format!(
            r#"{{"data":[{}],"has_more":true,"next_cursor":"n"}}"#,
            forward_json("f1")
        ),
    )
    .reply(
        200,
        &format!(
            r#"{{"data":[{}],"has_more":false,"next_cursor":null}}"#,
            forward_json("f2")
        ),
    );
    let ids: Vec<String> = fake
        .client()
        .addresses(AddressListParams::new().ticker("btc"))
        .map(|r| r.unwrap().id)
        .collect();
    assert_eq!(ids, ["f1", "f2"]);
    assert_eq!(query_of(&fake.req(1).url), "cursor=n&ticker=btc");
}

#[test]
fn the_paginator_works_for_unwrapped_list_endpoints() {
    let fake = Fake::new();
    fake.reply(
        200,
        r#"{"data":[{"id":"k1"}],"has_more":true,"next_cursor":"x"}"#,
    )
    .reply(
        200,
        r#"{"data":[{"id":"k2"}],"has_more":false,"next_cursor":null}"#,
    );
    let client = fake.client();
    let keys: Vec<serde_json::Value> = receivify::Paginator::new(None, |cursor| {
        let path = match cursor {
            Some(c) => format!("/api/v1/keys?cursor={c}"),
            None => "/api/v1/keys".to_string(),
        };
        client.request(Method::Get, &path, None, None)
    })
    .collect::<receivify::Result<_>>()
    .unwrap();
    assert_eq!(keys.len(), 2);
    assert_eq!(keys[1]["id"], "k2");
}

// ---------------------------------------------------------------- errors

#[test]
fn every_frozen_code_maps_to_its_own_variant() {
    for code in ErrorCode::ALL {
        let fake = Fake::new();
        fake.reply(
            400,
            &format!(r#"{{"error":"{}","detail":"d"}}"#, code.as_str()),
        );
        let err = fake.client().get_invoice("x").unwrap_err();
        let api = err.as_api().unwrap();
        assert_eq!(api.code, code);
        assert_eq!(api.raw_code, code.as_str());
        assert_eq!(ErrorCode::from_code(code.as_str()), code);
    }
}

#[test]
fn a_known_error_keeps_code_detail_status_and_request() {
    let fake = Fake::new();
    fake.reply(
        404,
        r#"{"error":"not_found","detail":"Invoice not found."}"#,
    );
    let err = fake.client().get_invoice("abc").unwrap_err();
    match &err {
        Error::Api(api) => {
            assert_eq!(api.code, ErrorCode::NotFound);
            assert_eq!(api.status, 404);
            assert_eq!(api.detail, "Invoice not found.");
            assert_eq!(api.request, "GET /api/v1/invoices/abc");
        }
        other => panic!("expected Api, got {other:?}"),
    }
    assert_eq!(err.status(), Some(404));
    assert!(err.to_string().contains("not_found"));
}

#[test]
fn an_unknown_error_code_falls_back_without_losing_the_code() {
    let fake = Fake::new();
    fake.reply(
        418,
        r#"{"error":"teapot_mode","detail":"Short and stout."}"#,
    );
    let err = fake.client().get_invoice("x").unwrap_err();
    let api = err.as_api().unwrap();
    assert_eq!(api.code, ErrorCode::Unknown);
    assert_eq!(api.raw_code, "teapot_mode");
    assert_eq!(api.detail, "Short and stout.");
    assert_eq!(api.status, 418);
}

#[test]
fn a_non_conforming_error_body_still_yields_an_api_error() {
    let fake = Fake::new();
    fake.reply(502, "<html>Bad gateway</html>");
    let client = fake.builder().max_retries(0).build().unwrap();
    let api = client.get_invoice("x").unwrap_err();
    let api = api.as_api().unwrap();
    assert_eq!(api.code, ErrorCode::Unknown);
    assert_eq!(api.raw_code, "");
    assert_eq!(api.status, 502);
    assert!(api.body.contains("Bad gateway"));
}

#[test]
fn a_429_carries_the_rate_limit_headers() {
    let fake = Fake::new();
    fake.reply_with_headers(
        429,
        r#"{"error":"rate_limited","detail":"Slow down."}"#,
        &[
            ("Retry-After", "7"),
            ("X-RateLimit-Reset", "1767225600"),
            ("X-RateLimit-Limit", "60"),
            ("X-RateLimit-Remaining", "0"),
        ],
    );
    let client = fake.builder().max_retries(0).build().unwrap();
    let err = client.get_invoice("x").unwrap_err();
    let api = err.as_api().unwrap();
    assert_eq!(api.code, ErrorCode::RateLimited);
    assert_eq!(api.retry_after, Some(7));
    assert_eq!(api.rate_limit_reset, Some(1767225600));
    assert_eq!(api.rate_limit_limit, Some(60));
    assert_eq!(api.rate_limit_remaining, Some(0));
    assert!(api.is_retryable());
    assert_eq!(api.header("x-ratelimit-limit"), Some("60"));
}

#[test]
fn an_error_message_never_contains_the_api_key() {
    let fake = Fake::new();
    fake.reply(
        401,
        r#"{"error":"unauthorized","detail":"Invalid API key."}"#,
    );
    let err = fake.client().get_invoice("x").unwrap_err();
    assert!(!err.to_string().contains(KEY));
    assert!(!format!("{err:?}").contains(KEY));
}

#[test]
fn a_2xx_body_of_the_wrong_shape_is_a_json_error() {
    let fake = Fake::new();
    fake.reply(200, r#"{"unexpected":true}"#);
    assert!(matches!(
        fake.client().get_invoice("x"),
        Err(Error::Json(_))
    ));
}

// ---------------------------------------------------------------- retries

#[test]
fn a_get_is_retried_on_500_then_succeeds() {
    let fake = Fake::new();
    fake.reply(500, r#"{"error":"internal","detail":"boom"}"#)
        .reply(200, &invoice_json("x"));
    fake.client().get_invoice("x").unwrap();
    assert_eq!(fake.count(), 2);
    assert_eq!(fake.sleeps.lock().unwrap().len(), 1);
}

#[test]
fn a_get_is_retried_on_429() {
    let fake = Fake::new();
    fake.reply(429, r#"{"error":"rate_limited","detail":"x"}"#)
        .reply(200, &invoice_json("x"));
    fake.client().get_invoice("x").unwrap();
    assert_eq!(fake.count(), 2);
}

#[test]
fn a_get_is_retried_on_a_transport_failure_then_reports_it() {
    let fake = Fake::new();
    fake.fail(TransportError::new("connection reset"))
        .fail(TransportError::timeout("timed out"))
        .fail(TransportError::timeout("timed out"));
    let err = fake.client().get_invoice("x").unwrap_err();
    assert_eq!(fake.count(), 3);
    match err {
        Error::Transport(t) => assert!(t.is_timeout()),
        other => panic!("expected Transport, got {other:?}"),
    }
}

#[test]
fn max_retries_bounds_the_attempts() {
    let fake = Fake::new();
    for _ in 0..10 {
        fake.reply(503, r#"{"error":"rate_unavailable","detail":"x"}"#);
    }
    let client = fake.builder().max_retries(4).build().unwrap();
    let err = client.get_invoice("x").unwrap_err();
    assert_eq!(err.code(), Some(ErrorCode::RateUnavailable));
    assert_eq!(fake.count(), 5);

    let fake = Fake::new();
    fake.reply(500, "{}").reply(200, &invoice_json("x"));
    let client = fake.builder().max_retries(0).build().unwrap();
    assert!(client.get_invoice("x").is_err());
    assert_eq!(fake.count(), 1);
}

#[test]
fn a_post_without_an_idempotency_key_is_never_retried() {
    let fake = Fake::new();
    fake.reply(500, r#"{"error":"internal","detail":"x"}"#)
        .reply(201, &invoice_json("x"));
    let err = fake
        .client()
        .create_invoice(&CreateInvoice::default(), None)
        .unwrap_err();
    assert_eq!(err.code(), Some(ErrorCode::Internal));
    assert_eq!(fake.count(), 1);

    let fake = Fake::new();
    fake.fail(TransportError::timeout("t"))
        .reply(201, &invoice_json("x"));
    let err = fake
        .client()
        .create_invoice(&CreateInvoice::default(), None)
        .unwrap_err();
    assert!(matches!(err, Error::Transport(_)));
    assert_eq!(fake.count(), 1);
}

#[test]
fn a_post_with_an_idempotency_key_is_retried_with_the_same_key() {
    let fake = Fake::new();
    fake.reply(502, r#"{"error":"upstream_error","detail":"x"}"#)
        .fail(TransportError::new("reset"))
        .reply(201, &invoice_json("x"));
    fake.client()
        .create_invoice(
            &CreateInvoice::default(),
            Some(&RequestOptions::idempotency_key("k-1")),
        )
        .unwrap();
    assert_eq!(fake.count(), 3);
    for i in 0..3 {
        assert_eq!(fake.req(i).header("idempotency-key"), Some("k-1"));
    }
}

#[test]
fn a_4xx_other_than_429_is_never_retried_even_on_a_get() {
    let fake = Fake::new();
    fake.reply(404, r#"{"error":"not_found","detail":"x"}"#)
        .reply(200, &invoice_json("x"));
    assert!(fake.client().get_invoice("x").is_err());
    assert_eq!(fake.count(), 1);
}

#[test]
fn backoff_honours_retry_after() {
    let fake = Fake::new();
    fake.reply_with_headers(
        429,
        r#"{"error":"rate_limited","detail":"x"}"#,
        &[("Retry-After", "3")],
    )
    .reply(200, &invoice_json("x"));
    fake.client().get_invoice("x").unwrap();
    assert_eq!(*fake.sleeps.lock().unwrap(), vec![Duration::from_secs(3)]);
}

#[test]
fn backoff_falls_back_to_x_ratelimit_reset() {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_secs();
    let reset = (now + 4).to_string();
    let fake = Fake::new();
    fake.reply_with_headers(
        429,
        r#"{"error":"rate_limited","detail":"x"}"#,
        &[("X-RateLimit-Reset", reset.as_str())],
    )
    .reply(200, &invoice_json("x"));
    fake.client().get_invoice("x").unwrap();
    let slept = fake.sleeps.lock().unwrap()[0];
    assert!(
        slept > Duration::from_secs(2) && slept <= Duration::from_secs(4),
        "{slept:?}"
    );
}

#[test]
fn exponential_backoff_grows_and_is_capped() {
    let fake = Fake::new();
    for _ in 0..4 {
        fake.reply(500, "{}");
    }
    let client = fake
        .builder()
        .max_retries(3)
        .retry_base_delay(Duration::from_millis(100))
        .build()
        .unwrap();
    let _ = client.get_invoice("x");
    let sleeps = fake.sleeps.lock().unwrap().clone();
    assert_eq!(sleeps.len(), 3);
    assert!(sleeps[0] >= Duration::from_millis(100) && sleeps[0] < Duration::from_millis(200));
    assert!(sleeps[1] >= Duration::from_millis(200) && sleeps[1] < Duration::from_millis(300));
    assert!(sleeps[2] >= Duration::from_millis(400) && sleeps[2] < Duration::from_millis(500));
}

// ---------------------------------------------------------------- the rest of the surface

#[test]
fn cancel_invoice_posts_to_the_cancel_route() {
    let fake = Fake::new();
    fake.reply(200, &invoice_json("x").replace("pending", "cancelled"));
    let inv = fake.client().cancel_invoice("x").unwrap();
    assert_eq!(inv.status, InvoiceStatus::Cancelled);
    assert_eq!(fake.last().method, Method::Post);
    assert_eq!(path_of(&fake.last().url), "/api/v1/invoices/x/cancel");
}

#[test]
fn bulk_cancel_sends_ids_and_action() {
    let fake = Fake::new();
    fake.reply(
        200,
        r#"{"ok":true,"action":"cancel","requested":2,"cancelled":1}"#,
    );
    let res = fake.client().bulk_cancel_invoices(&["a", "b"]).unwrap();
    assert_eq!((res.requested, res.cancelled), (2, 1));
    assert_eq!(
        fake.last_body_json(),
        serde_json::json!({"ids":["a","b"],"action":"cancel"})
    );
    let empty: [&str; 0] = [];
    assert!(matches!(
        fake.client().bulk_cancel_invoices(&empty),
        Err(Error::InvalidArgument(_))
    ));
}

#[test]
fn rotate_webhook_secret_returns_the_secret_once_and_masks_it_in_debug() {
    let fake = Fake::new();
    fake.reply(
        200,
        r#"{"secret":"whsec_new","rotated_at":"2026-01-01T00:00:00.000Z","prev_expires_at":"2026-01-02T00:00:00.000Z","grace_seconds":86400,"replaced_previous":false,"message":"Rotated."}"#,
    )
    .reply(
        200,
        r#"{"secret":null,"rotated_at":"2026-01-01T00:00:00.000Z","prev_expires_at":"2026-01-02T00:00:00.000Z","grace_seconds":86400,"replaced_previous":false,"message":"Replayed."}"#,
    );
    let client = fake.client();
    let opts = RequestOptions::idempotency_key("rotate-2026-01");
    let rotated = client.rotate_webhook_secret(Some(&opts)).unwrap();
    assert_eq!(rotated.secret.as_deref(), Some("whsec_new"));
    assert_eq!(rotated.grace_seconds, 86400);
    assert!(!format!("{rotated:?}").contains("whsec_new"));
    assert_eq!(fake.last().method, Method::Post);
    assert_eq!(path_of(&fake.last().url), "/api/v1/webhooks/secret/rotate");
    assert_eq!(
        fake.last().header("idempotency-key"),
        Some("rotate-2026-01")
    );

    let replay = client.rotate_webhook_secret(Some(&opts)).unwrap();
    assert_eq!(replay.secret, None);
}

#[test]
fn webhook_secret_state_decodes() {
    let fake = Fake::new();
    fake.reply(
        200,
        r#"{"rotated_at":null,"prev_expires_at":null,"prev_active":false}"#,
    );
    let state = fake.client().get_webhook_secret_state().unwrap();
    assert!(!state.prev_active);
}

#[test]
fn request_raw_exposes_status_and_idempotent_replay() {
    let fake = Fake::new();
    fake.reply_with_headers(200, &forward_json("f1"), &[("Idempotent-Replay", "true")]);
    let res = fake
        .client()
        .request_raw(
            Method::Post,
            "/api/v1/address",
            Some(&serde_json::json!({"ticker":"btc","destination":"bc1q"})),
            Some(&RequestOptions::idempotency_key("k")),
        )
        .unwrap();
    assert_eq!(res.status, 200);
    assert!(res.is_idempotent_replay());
    let fwd: receivify::Forward = res.json().unwrap();
    assert_eq!(fwd.id, "f1");
}

#[test]
fn a_204_with_no_body_decodes_as_null() {
    let fake = Fake::new();
    fake.reply(204, "");
    let v: serde_json::Value = fake
        .client()
        .request(Method::Delete, "/api/v1/webhooks/endpoints/x", None, None)
        .unwrap();
    assert!(v.is_null());
}

#[test]
fn request_rejects_a_relative_path() {
    let fake = Fake::new();
    let r: receivify::Result<serde_json::Value> =
        fake.client()
            .request(Method::Get, "api/v1/keys", None, None);
    assert!(matches!(r, Err(Error::InvalidArgument(_))));
}

#[test]
fn the_client_is_send_sync_and_clone() {
    fn assert_traits<T: Send + Sync + Clone>() {}
    assert_traits::<Client>();
}

#[cfg(not(feature = "ureq"))]
#[test]
fn without_the_ureq_feature_a_transport_is_required() {
    let err = Client::builder(BASE).build().unwrap_err();
    assert!(matches!(err, Error::Config(_)));
}

#[test]
fn a_redirect_is_not_followed_and_surfaces_as_an_error() {
    let fake = Fake::new();
    fake.reply_with_headers(302, "", &[("Location", "https://elsewhere.example/")]);
    let err = fake.client().get_invoice("x").unwrap_err();
    assert_eq!(err.status(), Some(302));
    assert_eq!(err.code(), Some(ErrorCode::Unknown));
}
