//! Money helpers.
//!
//! Every monetary field on the wire is an exact-integer base-unit **string**: satoshis for BTC,
//! wei for ETH, the coin's smallest unit otherwise. The SDK keeps those fields as `String` and
//! never routes them through `f64`, which cannot hold a satoshi count above 2^53, let alone a
//! 256-bit wei value.
//!
//! These helpers are opt-in, pure string arithmetic, and work for numbers of any size. `decimals`
//! comes from [`Invoice::decimals`](crate::Invoice::decimals) or
//! [`Ticker::decimals`](crate::Ticker::decimals).
//!
//! ```
//! use receivify::money::{format_units, parse_units, compare_amounts};
//!
//! assert_eq!(format_units("123456", 8).unwrap(), "0.00123456");
//! assert_eq!(parse_units("0.001", 8).unwrap(), "100000");
//! assert!(compare_amounts("100001", "100000").unwrap().is_gt());
//! ```

use std::cmp::Ordering;
use std::fmt;

/// A value passed to a money helper was not a valid amount.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MoneyError(String);

impl MoneyError {
    fn new(msg: impl Into<String>) -> Self {
        MoneyError(msg.into())
    }
}

impl fmt::Display for MoneyError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "receivify: {}", self.0)
    }
}

impl std::error::Error for MoneyError {}

const MAX_DECIMALS: u32 = 100;

fn check_decimals(decimals: u32) -> Result<usize, MoneyError> {
    if decimals > MAX_DECIMALS {
        return Err(MoneyError::new(format!(
            "decimals must be in 0..={MAX_DECIMALS}, got {decimals}"
        )));
    }
    Ok(decimals as usize)
}

/// Split an integer string into (negative, digits-without-leading-zeros).
fn split_integer(s: &str) -> Option<(bool, &str)> {
    let s = s.trim();
    let (neg, digits) = match s.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, s),
    };
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let stripped = digits.trim_start_matches('0');
    let stripped = if stripped.is_empty() { "0" } else { stripped };
    // "-0" is zero, not negative.
    Some((neg && stripped != "0", stripped))
}

/// True if `s` is an exact-integer base-unit string (`^-?[0-9]+$`).
pub fn is_base_units(s: &str) -> bool {
    split_integer(s).is_some()
}

/// Base units -> display decimal string. `format_units("123456", 8)` is `"0.00123456"`.
///
/// Always prints exactly `decimals` fraction digits (none when `decimals` is 0).
pub fn format_units(base: &str, decimals: u32) -> Result<String, MoneyError> {
    let d = check_decimals(decimals)?;
    let (neg, digits) = split_integer(base).ok_or_else(|| {
        MoneyError::new(format!(
            "format_units: expected an integer base-unit string, got {base:?}"
        ))
    })?;
    let sign = if neg { "-" } else { "" };
    if d == 0 {
        return Ok(format!("{sign}{digits}"));
    }
    let padded = if digits.len() <= d {
        format!("{}{}", "0".repeat(d + 1 - digits.len()), digits)
    } else {
        digits.to_string()
    };
    let (whole, frac) = padded.split_at(padded.len() - d);
    Ok(format!("{sign}{whole}.{frac}"))
}

/// Display decimal string -> base units. `parse_units("0.00123456", 8)` is `"123456"`.
///
/// Returns an error rather than rounding when `value` has more fraction digits than the asset.
pub fn parse_units(value: &str, decimals: u32) -> Result<String, MoneyError> {
    let d = check_decimals(decimals)?;
    let s = value.trim();
    let bad = || {
        MoneyError::new(format!(
            "parse_units: expected a decimal string, got {value:?}"
        ))
    };
    let (neg, body) = match s.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, s),
    };
    let (whole, frac) = match body.split_once('.') {
        Some((w, f)) => (w, f),
        None => (body, ""),
    };
    if (whole.is_empty() && frac.is_empty())
        || !whole.bytes().all(|b| b.is_ascii_digit())
        || !frac.bytes().all(|b| b.is_ascii_digit())
    {
        return Err(bad());
    }
    if frac.len() > d {
        return Err(MoneyError::new(format!(
            "parse_units: {value:?} has {} fraction digits but the asset has {d}; refusing to round a monetary value",
            frac.len()
        )));
    }
    let combined = format!(
        "{}{}{}",
        if whole.is_empty() { "0" } else { whole },
        frac,
        "0".repeat(d - frac.len())
    );
    let digits = combined.trim_start_matches('0');
    if digits.is_empty() {
        return Ok("0".to_string());
    }
    Ok(format!("{}{}", if neg { "-" } else { "" }, digits))
}

/// Compare two base-unit strings of any size, exactly.
pub fn compare_amounts(a: &str, b: &str) -> Result<Ordering, MoneyError> {
    fn parse(s: &str) -> Result<(bool, &str), MoneyError> {
        split_integer(s).ok_or_else(|| {
            MoneyError::new(format!(
                "compare_amounts: expected an integer base-unit string, got {s:?}"
            ))
        })
    }
    let (an, ad) = parse(a)?;
    let (bn, bd) = parse(b)?;
    let magnitude = ad.len().cmp(&bd.len()).then_with(|| ad.cmp(bd));
    Ok(match (an, bn) {
        (false, false) => magnitude,
        (true, true) => magnitude.reverse(),
        (true, false) => Ordering::Less,
        (false, true) => Ordering::Greater,
    })
}

/// Parse a base-unit string into a `u128` for arithmetic, when it fits.
/// A 256-bit wei value may not fit; prefer [`compare_amounts`] for comparisons.
pub fn to_u128(s: &str) -> Result<u128, MoneyError> {
    let (neg, digits) = split_integer(s).ok_or_else(|| {
        MoneyError::new(format!(
            "to_u128: expected an integer base-unit string, got {s:?}"
        ))
    })?;
    if neg {
        return Err(MoneyError::new(format!("to_u128: {s:?} is negative")));
    }
    digits
        .parse::<u128>()
        .map_err(|_| MoneyError::new(format!("to_u128: {s:?} does not fit in a u128")))
}

/// serde helpers: deserialize a money field as its exact text, from a JSON string OR number.
pub(crate) mod de {
    use serde::de::{self, Deserializer, MapAccess, Visitor};
    use std::fmt;

    // serde_json's `arbitrary_precision` hands a number to `deserialize_any` as a one-entry map
    // under this key, carrying the number's exact source text.
    const NUMBER_TOKEN: &str = "$serde_json::private::Number";

    struct MoneyVisitor;

    impl<'de> Visitor<'de> for MoneyVisitor {
        type Value = String;

        fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            f.write_str("an amount as a string or an exact JSON number")
        }

        fn visit_str<E: de::Error>(self, v: &str) -> Result<String, E> {
            Ok(v.to_string())
        }
        fn visit_string<E: de::Error>(self, v: String) -> Result<String, E> {
            Ok(v)
        }
        fn visit_u64<E: de::Error>(self, v: u64) -> Result<String, E> {
            Ok(v.to_string())
        }
        fn visit_i64<E: de::Error>(self, v: i64) -> Result<String, E> {
            Ok(v.to_string())
        }
        fn visit_u128<E: de::Error>(self, v: u128) -> Result<String, E> {
            Ok(v.to_string())
        }
        fn visit_i128<E: de::Error>(self, v: i128) -> Result<String, E> {
            Ok(v.to_string())
        }
        fn visit_f64<E: de::Error>(self, v: f64) -> Result<String, E> {
            // Only reachable if the exact text was already lost upstream. Refuse rather than guess.
            Err(E::custom(format!(
                "refusing to read money from a floating-point value ({v}); the exact amount is unknown"
            )))
        }
        fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<String, A::Error> {
            match map.next_key::<String>()? {
                Some(k) if k == NUMBER_TOKEN => map.next_value::<String>(),
                _ => Err(de::Error::custom("expected an amount, found an object")),
            }
        }
    }

    struct OptMoneyVisitor;

    impl<'de> Visitor<'de> for OptMoneyVisitor {
        type Value = Option<String>;

        fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            f.write_str("an amount, or null")
        }
        fn visit_none<E: de::Error>(self) -> Result<Option<String>, E> {
            Ok(None)
        }
        fn visit_unit<E: de::Error>(self) -> Result<Option<String>, E> {
            Ok(None)
        }
        fn visit_some<D: Deserializer<'de>>(self, d: D) -> Result<Option<String>, D::Error> {
            d.deserialize_any(MoneyVisitor).map(Some)
        }
    }

    /// Required money field.
    pub fn money<'de, D: Deserializer<'de>>(d: D) -> Result<String, D::Error> {
        d.deserialize_any(MoneyVisitor)
    }

    /// Optional / nullable money field. Pair with `#[serde(default)]`.
    pub fn opt_money<'de, D: Deserializer<'de>>(d: D) -> Result<Option<String>, D::Error> {
        d.deserialize_option(OptMoneyVisitor)
    }
}
