package receivify

import "time"

// Conventions used by every type in this file:
//
//   - Monetary amounts are strings. Fields documented as "base units" hold an exact integer
//     (satoshis, wei, ...) such as "100000". Fields documented as "decimal" hold a display
//     value such as "25.00". Never convert either to float64; use FormatUnits / ParseUnits.
//   - A JSON null decodes to the zero value: "" for strings, nil for *time.Time.
//   - Status and event values are open enums. New values are additive, so keep a default branch.

// List is the one list envelope every list endpoint returns.
type List[T any] struct {
	// Data is the page, newest first.
	Data []T `json:"data"`
	// HasMore is a fact from the server (it reads limit+1 rows); never infer it from len(Data).
	HasMore bool `json:"has_more"`
	// NextCursor is the cursor for the next page, "" exactly when HasMore is false.
	NextCursor string `json:"next_cursor"`
}

// InvoiceStatus is the invoice lifecycle state. Open enum.
type InvoiceStatus string

// Invoice statuses.
const (
	InvoiceStatusPending        InvoiceStatus = "pending"
	InvoiceStatusPartial        InvoiceStatus = "partial"
	InvoiceStatusConfirming     InvoiceStatus = "confirming"
	InvoiceStatusPaid           InvoiceStatus = "paid"
	InvoiceStatusSwept          InvoiceStatus = "swept"
	InvoiceStatusExpired        InvoiceStatus = "expired"
	InvoiceStatusPartialExpired InvoiceStatus = "partial_expired"
	InvoiceStatusCancelled      InvoiceStatus = "cancelled"
	InvoiceStatusFailed         InvoiceStatus = "failed"
)

// Invoice is identical on create, retrieve, list and cancel.
type Invoice struct {
	ID string `json:"id"`
	// Reference is your business key. It also de-duplicates: reusing a reference with the same
	// terms returns the EXISTING invoice. "" when not set.
	Reference string `json:"reference"`
	// Asset is the lower-case symbol, e.g. "btc", "usdc".
	Asset string `json:"asset"`
	// AmountExpected is what the payer must send, in exact-integer base units. Authoritative.
	AmountExpected string `json:"amount_expected"`
	// AmountReceived is base units received so far; "0" until something arrives.
	AmountReceived string `json:"amount_received"`
	// AmountFiat is a decimal display price such as "25.00", "" when not fiat-priced.
	// Display only: never settle against it.
	AmountFiat string `json:"amount_fiat"`
	// FiatCurrency is the ISO-4217 code of AmountFiat, "" when not fiat-priced.
	FiatCurrency string `json:"fiat_currency"`
	// Chain is the EVM chain, "solana" or "tron", or "" for BTC and BTCPay-settled coins.
	Chain string `json:"chain"`
	// Provider is the settlement backend: bitcoind, btcpay, evm, solana, tron.
	Provider string `json:"provider"`
	// Address is the deposit address. Send exactly this asset on exactly this chain.
	Address string        `json:"address"`
	Status  InvoiceStatus `json:"status"`
	// ExpiresAt is when the invoice stops accepting payment.
	ExpiresAt time.Time  `json:"expires_at"`
	PaidAt    *time.Time `json:"paid_at"`
	CreatedAt time.Time  `json:"created_at"`
	// Metadata is whatever you stored; numbers inside it decode as json.Number.
	Metadata map[string]any `json:"metadata"`
	// CheckoutURL is the hosted checkout page. Redirect the payer here.
	CheckoutURL string `json:"checkout_url"`
	// Decimals is the asset's decimal places: the divisor for AmountExpected.
	Decimals int `json:"decimals"`
	// AmountDecimal is AmountExpected already divided by 10^Decimals, as a decimal string.
	// Display only; keep using AmountExpected for comparisons and arithmetic.
	AmountDecimal string `json:"amount_decimal"`
	// PaymentURI is a wallet-scannable URI for a QR code, or "" when the chain has no safe one
	// (EVM, Solana, Tron): show the bare Address instead.
	PaymentURI string `json:"payment_uri"`
	// AddFeeOntop reports whether the commission was added on top of the requested amount.
	AddFeeOntop bool `json:"add_fee_ontop"`
	// AmountNetRequested is what you asked to RECEIVE, in base units. Equal to AmountExpected
	// when AddFeeOntop is false.
	AmountNetRequested string `json:"amount_net_requested"`
	// FeeOntop is AmountExpected - AmountNetRequested in base units; "0" when AddFeeOntop is false.
	FeeOntop string `json:"fee_ontop"`
}

// CreateInvoiceParams is the body of POST /api/v1/invoices.
//
// Exactly one amount field is required. They are tried in this order and the first present one
// wins: AmountFiat (+Currency), Amount, AmountSats, AmountBTC.
type CreateInvoiceParams struct {
	// Asset defaults to "btc" on the server.
	Asset string `json:"asset,omitempty"`
	// Chain is only consulted for EVM assets (e.g. "base", "ethereum", "arbitrum").
	Chain string `json:"chain,omitempty"`
	// Amount is a decimal amount in the coin's own units, e.g. "0.001".
	Amount string `json:"amount,omitempty"`
	// AmountFiat is a decimal fiat price, e.g. "25.00", converted at the current rate and locked.
	AmountFiat string `json:"amount_fiat,omitempty"`
	// Currency is the ISO-4217 code for AmountFiat. Defaults to USD on the server.
	Currency string `json:"currency,omitempty"`
	// AmountSats is exact base units as an integer string. Applies to every asset despite the name.
	AmountSats string `json:"amount_sats,omitempty"`
	// AmountBTC is legacy and only honoured when Asset is "btc".
	AmountBTC string `json:"amount_btc,omitempty"`
	// Reference is your business key, e.g. "shop1-order-1001". Make it unique per order.
	Reference string `json:"reference,omitempty"`
	// CallbackURL is a per-invoice webhook target, in addition to account-level endpoints.
	CallbackURL string `json:"callback_url,omitempty"`
	// Metadata is stored on the invoice and returned verbatim.
	Metadata map[string]any `json:"metadata,omitempty"`
	// AddFeeOntop: nil (omitted) uses the account default; receivify.Bool(true) makes the payer
	// cover the commission so you receive the full amount; receivify.Bool(false) forces
	// fee-out-of-amount for this invoice regardless of the account default.
	AddFeeOntop *bool `json:"add_fee_ontop,omitempty"`
}

// ListInvoicesParams filters GET /api/v1/invoices.
type ListInvoicesParams struct {
	// Limit is 1..200 (larger is clamped). Zero means the server default.
	Limit int
	// Cursor is a NextCursor from a previous page.
	Cursor string
	// Status is an exact-match filter. Not validated: an unknown value matches nothing.
	Status InvoiceStatus
}

// BulkResult is the outcome of BulkCancelInvoices.
type BulkResult struct {
	OK     bool   `json:"ok"`
	Action string `json:"action"`
	// Requested is how many ids were submitted.
	Requested int `json:"requested"`
	// Cancelled is how many were actually eligible and changed.
	Cancelled int `json:"cancelled"`
}

// PublicInvoice is the unauthenticated checkout-page projection of an invoice.
type PublicInvoice struct {
	ID             string        `json:"id"`
	Status         InvoiceStatus `json:"status"`
	Address        string        `json:"address"`
	AmountExpected string        `json:"amount_expected"`
	AmountReceived string        `json:"amount_received"`
	ExpiresAt      time.Time     `json:"expires_at"`
}

// ForwardStatus is a forwarding address state. Open enum.
type ForwardStatus string

// Forward statuses.
const (
	ForwardStatusActive   ForwardStatus = "active"
	ForwardStatusPaused   ForwardStatus = "paused"
	ForwardStatusDisabled ForwardStatus = "disabled"
	ForwardStatusExpired  ForwardStatus = "expired"
)

// Forward is a standing forwarding (deposit) address that sends everything it receives to
// Destination.
type Forward struct {
	// ID is the canonical identifier.
	ID string `json:"id"`
	// ForwardID carries the same value as ID.
	//
	// Deprecated: read ID.
	ForwardID string `json:"forward_id"`
	// Ticker is exactly as supplied at create time, lower-cased.
	Ticker   string `json:"ticker"`
	Provider string `json:"provider"`
	// Chain is "" for btc and the BTCPay-settled coins.
	Chain string `json:"chain"`
	// Address is the deposit address to hand to payers.
	Address string `json:"address"`
	// Destination is your wallet. Immutable.
	Destination string `json:"destination"`
	// CallbackURL receives forward.* events only. "" when unset.
	CallbackURL           string `json:"callback_url"`
	ConfirmationsRequired int    `json:"confirmations_required"`
	// MinForward is a DISPLAY-unit decimal string such as "0.500000", NOT base units.
	MinForward string        `json:"min_forward"`
	Status     ForwardStatus `json:"status"`
	// ExpiresAt is nil for every permanent address.
	ExpiresAt *time.Time `json:"expires_at"`
	CreatedAt time.Time  `json:"created_at"`
	UpdatedAt *time.Time `json:"updated_at"`

	// Created is set by CreateAddress only: true when a new address was derived (HTTP 201),
	// false when an existing forward with the same identity was returned (HTTP 200).
	Created bool `json:"-"`
}

// ForwardPayment is one deposit credited to a forwarding address.
type ForwardPayment struct {
	TxidIn string `json:"txid_in"`
	// Value is the gross amount received, in base units.
	Value         string    `json:"value"`
	Status        string    `json:"status"`
	TxidOut       string    `json:"txid_out"`
	Confirmations int       `json:"confirmations"`
	CreatedAt     time.Time `json:"created_at"`
}

// ForwardCallback is one webhook delivery attempt for a forward.
type ForwardCallback struct {
	Event       string     `json:"event"`
	URL         string     `json:"url"`
	Status      string     `json:"status"`
	Attempts    int        `json:"attempts"`
	LastStatus  *int       `json:"last_status"`
	LastError   string     `json:"last_error"`
	CreatedAt   time.Time  `json:"created_at"`
	DeliveredAt *time.Time `json:"delivered_at"`
}

// ForwardDetail is GET /api/v1/address/{id}: the Forward plus its capped logs.
type ForwardDetail struct {
	Forward
	// Payments is newest first, capped at 200. Not paginated.
	Payments         []ForwardPayment  `json:"payments"`
	PaymentsHasMore  bool              `json:"payments_has_more"`
	Callbacks        []ForwardCallback `json:"callbacks"`
	CallbacksHasMore bool              `json:"callbacks_has_more"`
}

// CreateAddressParams is the body of POST /api/v1/address.
type CreateAddressParams struct {
	// Ticker, e.g. "btc", "usdc_base". See ListTickers.
	Ticker string `json:"ticker"`
	// Destination is your wallet; validated against the chain's address format.
	Destination string `json:"destination"`
	// Callback receives forward.* events. Part of the address's identity: a different callback
	// creates a different forward.
	Callback string `json:"callback,omitempty"`
	// Confirmations before a payment is credited. Zero uses the deployment default.
	Confirmations int            `json:"confirmations,omitempty"`
	Metadata      map[string]any `json:"metadata,omitempty"`
}

// UpdateAddressParams is the body of PATCH /api/v1/address/{id}. Only these fields are mutable.
type UpdateAddressParams struct {
	// Status is "active" or "paused". Use DisableAddress for "disabled".
	Status ForwardStatus `json:"status,omitempty"`
	// CallbackURL: nil leaves it unchanged, receivify.String("") clears it.
	CallbackURL *string `json:"callback_url,omitempty"`
}

// ListAddressesParams filters GET /api/v1/address.
type ListAddressesParams struct {
	Limit  int
	Cursor string
	// Status is validated: an unknown value is a 400 bad_request.
	Status ForwardStatus
	Ticker string
}

// Ticker is one (asset, chain) pair offered for forwarding.
type Ticker struct {
	// Ticker is the string to pass as CreateAddressParams.Ticker.
	Ticker   string `json:"ticker"`
	Provider string `json:"provider"`
	Chain    string `json:"chain"`
	Name     string `json:"name"`
	// Decimals is the power of ten to divide a base-unit amount by for display.
	Decimals int `json:"decimals"`
}

// Asset is display metadata for an invoiceable coin.
type Asset struct {
	Symbol   string `json:"symbol"`
	Name     string `json:"name"`
	Network  string `json:"network"`
	Color    string `json:"color"`
	Provider string `json:"provider"`
	// Min is a display decimal OR the literal word "network". Not always parseable.
	Min string `json:"min"`
	// NetworkFee is English prose, display only.
	NetworkFee string `json:"network_fee"`
	Live       bool   `json:"live"`
}

// WebhookSecretState is rotation metadata. It never carries a secret.
type WebhookSecretState struct {
	RotatedAt     *time.Time `json:"rotated_at"`
	PrevExpiresAt *time.Time `json:"prev_expires_at"`
	// PrevActive: while true, your receiver must accept either secret.
	PrevActive bool `json:"prev_active"`
}

// WebhookSecretRotated is the result of RotateWebhookSecret.
type WebhookSecretRotated struct {
	// Secret is the new signing secret, shown ONCE. "" on an Idempotency-Key replay.
	Secret    string    `json:"secret"`
	RotatedAt time.Time `json:"rotated_at"`
	// PrevExpiresAt: until then, verify against [new, previous].
	PrevExpiresAt    *time.Time `json:"prev_expires_at"`
	GraceSeconds     int        `json:"grace_seconds"`
	ReplacedPrevious bool       `json:"replaced_previous"`
	Message          string     `json:"message"`
}

// WebhookEndpoint is an account-level webhook target.
type WebhookEndpoint struct {
	ID        string     `json:"id"`
	URL       string     `json:"url"`
	Active    bool       `json:"active"`
	CreatedAt time.Time  `json:"created_at"`
	UpdatedAt *time.Time `json:"updated_at"`
}

// UpdateWebhookEndpointParams is the body of PATCH /api/v1/webhooks/endpoints/{id}.
type UpdateWebhookEndpointParams struct {
	Active *bool  `json:"active,omitempty"`
	URL    string `json:"url,omitempty"`
}

// WebhookDelivery is one row of the delivery log.
type WebhookDelivery struct {
	// ID is a 24-character hex id; pass it to ReplayWebhookDelivery.
	ID          string     `json:"id"`
	InvoiceID   string     `json:"invoice_id"`
	ForwardID   string     `json:"forward_id"`
	Event       string     `json:"event"`
	Status      string     `json:"status"`
	Attempts    int        `json:"attempts"`
	URL         string     `json:"url"`
	LastStatus  *int       `json:"last_status"`
	LastError   string     `json:"last_error"`
	CreatedAt   time.Time  `json:"created_at"`
	DeliveredAt *time.Time `json:"delivered_at"`
}

// ListWebhookDeliveriesParams filters GET /api/v1/webhooks/deliveries.
type ListWebhookDeliveriesParams struct {
	Limit  int
	Cursor string
	// Event is validated against the event registry.
	Event string
	// Status is pending, delivered or failed.
	Status string
}

// WebhookTestResult is the outcome of SendTestWebhook. The call returns 200 even when every
// delivery failed: compare Delivered with Total.
type WebhookTestResult struct {
	Event     string `json:"event"`
	Delivered int    `json:"delivered"`
	Total     int    `json:"total"`
	Results   []struct {
		URL    string `json:"url"`
		OK     bool   `json:"ok"`
		Status *int   `json:"status"`
		Error  string `json:"error,omitempty"`
	} `json:"results"`
}

// PaymentLink is a reusable payment link.
type PaymentLink struct {
	ID    string `json:"id"`
	Title string `json:"title"`
	// AmountSats is base units for a fixed BTC-priced link, "" otherwise.
	AmountSats string `json:"amount_sats"`
	// AmountFiat is a decimal fiat price, "" otherwise.
	AmountFiat   string     `json:"amount_fiat"`
	FiatCurrency string     `json:"fiat_currency"`
	OpenAmount   bool       `json:"open_amount"`
	Active       bool       `json:"active"`
	Uses         int        `json:"uses"`
	CallbackURL  string     `json:"callback_url"`
	CreatedAt    time.Time  `json:"created_at"`
	UpdatedAt    *time.Time `json:"updated_at"`
	URL          string     `json:"url"`
}

// CreatePaymentLinkParams is the body of POST /api/v1/payment-links. Omit both amounts for an
// open-amount link.
type CreatePaymentLinkParams struct {
	Title string `json:"title,omitempty"`
	// AmountBTC is a decimal BTC amount. Mutually exclusive with AmountFiat.
	AmountBTC string `json:"amount_btc,omitempty"`
	// AmountFiat is a decimal fiat price such as "20.00".
	AmountFiat  string `json:"amount_fiat,omitempty"`
	Currency    string `json:"currency,omitempty"`
	CallbackURL string `json:"callback_url,omitempty"`
}

// UpdatePaymentLinkParams is the body of PATCH /api/v1/payment-links/{id}.
type UpdatePaymentLinkParams struct {
	Active *bool   `json:"active,omitempty"`
	Title  *string `json:"title,omitempty"`
	// AmountBTC: receivify.String("") converts a fixed link into an open-amount one.
	AmountBTC *string `json:"amount_btc,omitempty"`
}

// ListPaymentLinksParams filters GET /api/v1/payment-links.
type ListPaymentLinksParams struct {
	Limit  int
	Cursor string
	Active *bool
}

// PublicPaymentLink is the unauthenticated view of a payment link.
type PublicPaymentLink struct {
	ID         string `json:"id"`
	Title      string `json:"title"`
	OpenAmount bool   `json:"open_amount"`
	// AmountBTC is a decimal BTC string, NOT base units. "" for an open-amount link.
	AmountBTC    string `json:"amount_btc"`
	AmountFiat   string `json:"amount_fiat"`
	FiatCurrency string `json:"fiat_currency"`
	Assets       []struct {
		Symbol   string `json:"symbol"`
		Name     string `json:"name"`
		Color    string `json:"color"`
		Decimals int    `json:"decimals"`
	} `json:"assets"`
	// FX holds advisory floating-point rates. Display only; rates are not money.
	FX struct {
		Crypto map[string]float64 `json:"crypto"`
		Fiat   map[string]float64 `json:"fiat"`
	} `json:"fx"`
}

// RecurringSchedule generates an invoice every IntervalDays.
type RecurringSchedule struct {
	ID    string `json:"id"`
	Title string `json:"title"`
	Asset string `json:"asset"`
	// AmountFiat is a decimal string, "" when priced in the coin.
	AmountFiat string `json:"amount_fiat"`
	Currency   string `json:"currency"`
	// Amount is a decimal coin amount (not base units), "" when priced in fiat.
	Amount        string    `json:"amount"`
	IntervalDays  int       `json:"interval_days"`
	Active        bool      `json:"active"`
	Runs          int       `json:"runs"`
	NextRun       time.Time `json:"next_run"`
	LastInvoiceID string    `json:"last_invoice_id"`
	CallbackURL   string    `json:"callback_url"`
	CreatedAt     time.Time `json:"created_at"`
}

// CreateRecurringParams is the body of POST /api/v1/recurring. Supply AmountFiat or Amount.
type CreateRecurringParams struct {
	Title        string `json:"title,omitempty"`
	Asset        string `json:"asset,omitempty"`
	AmountFiat   string `json:"amount_fiat,omitempty"`
	Currency     string `json:"currency,omitempty"`
	Amount       string `json:"amount,omitempty"`
	IntervalDays int    `json:"interval_days"`
	CallbackURL  string `json:"callback_url,omitempty"`
}

// APIKey is a listed key. The secret never appears here.
type APIKey struct {
	// ID is what RevokeAPIKey takes, NOT the prefix.
	ID         string     `json:"id"`
	Prefix     string     `json:"prefix"`
	Label      string     `json:"label"`
	CreatedAt  time.Time  `json:"created_at"`
	LastUsedAt *time.Time `json:"last_used_at"`
	Revoked    bool       `json:"revoked"`
}

// APIKeyCreated is the result of CreateAPIKey.
type APIKeyCreated struct {
	ID string `json:"id"`
	// APIKey is the raw secret, shown ONCE. "" on an Idempotency-Key replay.
	APIKey    string     `json:"api_key"`
	Prefix    string     `json:"prefix"`
	Label     string     `json:"label"`
	CreatedAt *time.Time `json:"created_at"`
	Message   string     `json:"message"`
}

// PageParams is the plain limit/cursor pair used by several list endpoints.
type PageParams struct {
	Limit  int
	Cursor string
}

// Settings are the merchant account settings.
type Settings struct {
	DisplayCurrency string `json:"display_currency"`
	Email           string `json:"email"`
	// FeeBps is a rate in basis points, an integer.
	FeeBps int `json:"fee_bps"`
	// AddFeeOntop is the account default for CreateInvoiceParams.AddFeeOntop.
	AddFeeOntop bool `json:"add_fee_ontop"`
}

// UpdateSettingsParams is the body of PATCH /api/v1/settings.
type UpdateSettingsParams struct {
	DisplayCurrency string `json:"display_currency,omitempty"`
	AddFeeOntop     *bool  `json:"add_fee_ontop,omitempty"`
}

// SettingsUpdateAck echoes only the fields that were written.
type SettingsUpdateAck struct {
	OK              bool   `json:"ok"`
	DisplayCurrency string `json:"display_currency,omitempty"`
	AddFeeOntop     *bool  `json:"add_fee_ontop,omitempty"`
}

// FxSnapshot holds ADVISORY floating-point rates. Never settle against them.
type FxSnapshot struct {
	Base      string             `json:"base"`
	Crypto    map[string]float64 `json:"crypto"`
	Fiat      map[string]float64 `json:"fiat"`
	FetchedAt *time.Time         `json:"fetched_at"`
	Source    string             `json:"source"`
	Stale     bool               `json:"stale"`
}

// Summary is the dashboard composite.
type Summary struct {
	Merchant struct {
		ID              string            `json:"id"`
		Email           string            `json:"email"`
		PayoutAddress   string            `json:"payout_address"`
		PayoutAddresses map[string]string `json:"payout_addresses"`
		FeeBps          int               `json:"fee_bps"`
		APIKeyPrefix    string            `json:"api_key_prefix"`
		DisplayCurrency string            `json:"display_currency"`
	} `json:"merchant"`
	Counts  map[string]int `json:"counts"`
	ByAsset []struct {
		Symbol    string `json:"symbol"`
		Name      string `json:"name"`
		Color     string `json:"color"`
		Decimals  int    `json:"decimals"`
		Count     int    `json:"count"`
		PaidCount int    `json:"paid_count"`
		// VolumeBase, FeeBase and NetBase are base units.
		VolumeBase string `json:"volume_base"`
		FeeBase    string `json:"fee_base"`
		NetBase    string `json:"net_base"`
	} `json:"by_asset"`
	ByStatus map[string]int `json:"by_status"`
	Series   []struct {
		Day    string            `json:"day"`
		Assets map[string]string `json:"assets"`
	} `json:"series"`
	FX         FxSnapshot `json:"fx"`
	Currencies []string   `json:"currencies"`
}

// PayoutChange is a staged payout-address change.
type PayoutChange struct {
	ID          string     `json:"id"`
	Asset       string     `json:"asset"`
	NewAddress  string     `json:"new_address"`
	OldAddress  string     `json:"old_address"`
	Status      string     `json:"status"`
	RequestedAt time.Time  `json:"requested_at"`
	EffectiveAt time.Time  `json:"effective_at"`
	ConfirmedAt *time.Time `json:"confirmed_at"`
	AppliedAt   *time.Time `json:"applied_at"`
}

// PayoutState is the live payout map plus every open staged change.
type PayoutState struct {
	PayoutAddresses   map[string]string `json:"payout_addresses"`
	Pending           []PayoutChange    `json:"pending"`
	TimelockHours     int               `json:"timelock_hours"`
	EmailConfirmation bool              `json:"email_confirmation"`
	Instant           bool              `json:"instant"`
}

// PayoutStageAck is the result of StagePayoutChanges.
type PayoutStageAck struct {
	OK                bool           `json:"ok"`
	Staged            []PayoutChange `json:"staged"`
	Applied           []PayoutChange `json:"applied"`
	Pending           []PayoutChange `json:"pending"`
	TimelockHours     int            `json:"timelock_hours"`
	EmailConfirmation bool           `json:"email_confirmation"`
	Message           string         `json:"message"`
}

// Health is the operator health report, returned on both 200 and 503.
type Health struct {
	// Status is ok, warn or degraded.
	Status string                    `json:"status"`
	Time   time.Time                 `json:"time"`
	Checks map[string]map[string]any `json:"checks"`
	Alerts []map[string]any          `json:"alerts"`
}

// Ack is the generic acknowledgement shape: {"ok": true, "<verb>": "<id>"}.
type Ack struct {
	OK        bool   `json:"ok"`
	Cancelled string `json:"cancelled,omitempty"`
	Revoked   string `json:"revoked,omitempty"`
	Deleted   string `json:"deleted,omitempty"`
	Replayed  string `json:"replayed,omitempty"`
}
