//! Create an invoice and print the checkout URL.
//!
//! ```sh
//! RECEIVIFY_URL=https://receivify.io RECEIVIFY_API_KEY=rcv_live_... cargo run --example quickstart
//! ```

use std::time::Duration;

use receivify::{Client, CreateInvoice, ErrorCode, RequestOptions};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let base_url = std::env::var("RECEIVIFY_URL").unwrap_or_else(|_| "https://receivify.io".into());
    let client = Client::builder(base_url)
        .api_key(std::env::var("RECEIVIFY_API_KEY")?)
        .timeout(Duration::from_secs(30))
        .build()?;

    let order_id = "order-1001";
    let result = client.create_invoice(
        &CreateInvoice {
            amount_fiat: Some("25.00".into()),
            currency: Some("USD".into()),
            asset: Some("btc".into()),
            reference: Some(order_id.into()),
            ..Default::default()
        },
        Some(&RequestOptions::idempotency_key(order_id)),
    );

    match result {
        Ok(invoice) => {
            println!("invoice {} ({})", invoice.id, invoice.status);
            println!(
                "pay {} {} to {}",
                invoice
                    .amount_decimal
                    .as_deref()
                    .unwrap_or(&invoice.amount_expected),
                invoice.asset,
                invoice.address
            );
            println!("redirect the payer to {}", invoice.checkout_url);
        }
        Err(e) => match e.code() {
            Some(ErrorCode::Unauthorized) => eprintln!("check RECEIVIFY_API_KEY"),
            Some(ErrorCode::AlreadyExists) => eprintln!("reference reused with different terms"),
            _ => return Err(e.into()),
        },
    }
    Ok(())
}
