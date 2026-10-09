//! A fake transport: records every request and replays canned responses. No network.
#![allow(dead_code)]

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use receivify::{Client, HttpRequest, HttpResponse, Transport, TransportError};

pub const BASE: &str = "https://pay.example.test";
pub const KEY: &str = "rcv_live_supersecretkey123";

type Reply = Result<HttpResponse, TransportError>;

#[derive(Clone, Default)]
pub struct Fake {
    pub requests: Arc<Mutex<Vec<HttpRequest>>>,
    replies: Arc<Mutex<VecDeque<Reply>>>,
    pub sleeps: Arc<Mutex<Vec<Duration>>>,
}

impl Fake {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn reply(&self, status: u16, body: &str) -> &Self {
        self.reply_with_headers(status, body, &[])
    }

    pub fn reply_with_headers(&self, status: u16, body: &str, headers: &[(&str, &str)]) -> &Self {
        let mut h: Vec<(String, String)> = headers
            .iter()
            .map(|(k, v)| (k.to_string(), v.to_string()))
            .collect();
        h.push(("content-type".into(), "application/json".into()));
        self.replies.lock().unwrap().push_back(Ok(HttpResponse {
            status,
            headers: h,
            body: body.as_bytes().to_vec(),
        }));
        self
    }

    pub fn fail(&self, err: TransportError) -> &Self {
        self.replies.lock().unwrap().push_back(Err(err));
        self
    }

    pub fn count(&self) -> usize {
        self.requests.lock().unwrap().len()
    }

    pub fn req(&self, i: usize) -> HttpRequest {
        self.requests.lock().unwrap()[i].clone()
    }

    pub fn last(&self) -> HttpRequest {
        self.requests
            .lock()
            .unwrap()
            .last()
            .cloned()
            .expect("no request was made")
    }

    pub fn last_body_json(&self) -> serde_json::Value {
        serde_json::from_slice(self.last().body.as_deref().unwrap_or(b"null")).unwrap()
    }

    pub fn client(&self) -> Client {
        self.builder().build().unwrap()
    }

    pub fn builder(&self) -> receivify::ClientBuilder {
        let sleeps = self.sleeps.clone();
        Client::builder(BASE)
            .api_key(KEY)
            .transport(self.clone())
            .sleeper(move |d| sleeps.lock().unwrap().push(d))
    }
}

impl Transport for Fake {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, TransportError> {
        self.requests.lock().unwrap().push(request.clone());
        self.replies
            .lock()
            .unwrap()
            .pop_front()
            .unwrap_or_else(|| Err(TransportError::new("fake transport: no reply queued")))
    }
}

pub fn path_of(url: &str) -> String {
    let rest = url.strip_prefix(BASE).unwrap_or(url);
    rest.split('?').next().unwrap().to_string()
}

pub fn query_of(url: &str) -> String {
    url.split_once('?')
        .map(|(_, q)| q.to_string())
        .unwrap_or_default()
}

pub fn invoice_json(id: &str) -> String {
    format!(
        r#"{{"id":"{id}","reference":"order-1","asset":"btc","amount_expected":"100000","amount_received":"0","amount_fiat":null,"fiat_currency":null,"chain":null,"provider":"bitcoind","address":"bc1qexample","status":"pending","expires_at":"2026-01-01T01:00:00.000Z","paid_at":null,"created_at":"2026-01-01T00:00:00.000Z","metadata":{{}},"checkout_url":"https://pay.example.test/pay/{id}","decimals":8,"amount_decimal":"0.00100000","payment_uri":"bitcoin:bc1qexample?amount=0.00100000","add_fee_ontop":false,"amount_net_requested":"100000","fee_ontop":"0"}}"#
    )
}

pub fn page_json(ids: &[&str], has_more: bool, next: Option<&str>) -> String {
    let items: Vec<String> = ids.iter().map(|id| invoice_json(id)).collect();
    format!(
        r#"{{"data":[{}],"has_more":{},"next_cursor":{}}}"#,
        items.join(","),
        has_more,
        next.map(|c| format!("\"{c}\""))
            .unwrap_or_else(|| "null".into())
    )
}

pub fn forward_json(id: &str) -> String {
    format!(
        r#"{{"id":"{id}","forward_id":"{id}","ticker":"btc","provider":"bitcoind","chain":null,"address":"bc1qdeposit","destination":"bc1qdest","callback_url":"https://shop.example/hook","confirmations_required":2,"min_forward":"0.00010000","status":"active","expires_at":null,"created_at":"2026-01-01T00:00:00.000Z","updated_at":null}}"#
    )
}
