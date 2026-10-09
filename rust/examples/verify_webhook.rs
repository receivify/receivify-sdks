//! Verify and parse a webhook delivery. Runs offline: it signs a sample body locally the way
//! Receivify does, then verifies it exactly as a real receiver would.
//!
//! In a real handler, `raw_body` is the request body as bytes (before any JSON parsing) and
//! `header` is the `X-Receivify-Signature` header.

use receivify::webhooks::{self, VerifyOptions};

fn main() {
    let current = "whsec_current";
    let previous = "whsec_previous"; // only during a rotation grace window

    let raw_body = br#"{"event":"invoice.paid","sequence":12,"created_at":"2026-08-24T12:05:00.000Z","data":{"id":"11111111-2222-4333-8444-555555555555","reference":"order-1001","status":"paid","amount_expected":"100000","amount_received":"100000","address":"bc1qexample","paid_at":"2026-08-24T12:05:00.000Z"}}"#;
    let header = webhooks::sign(raw_body, current, webhooks::now_unix());

    match webhooks::parse(
        raw_body,
        &header,
        &[current, previous],
        VerifyOptions::default(),
    ) {
        Ok(event) => match event.event.as_str() {
            "invoice.paid" => {
                let data = event.invoice_data().expect("invoice-shaped data");
                println!(
                    "order {} paid: {} base units (sequence {})",
                    data.reference.unwrap_or_default(),
                    data.amount_received,
                    event.sequence
                );
            }
            other => println!("ignoring {other}"),
        },
        Err(e) => eprintln!("rejected webhook: {} ({e})", e.reason()),
    }
}
