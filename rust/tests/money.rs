use std::cmp::Ordering;

use receivify::money::{compare_amounts, format_units, is_base_units, parse_units, to_u128};

#[test]
fn format_units_renders_satoshis_without_a_float() {
    assert_eq!(format_units("123456", 8).unwrap(), "0.00123456");
    assert_eq!(format_units("100000", 8).unwrap(), "0.00100000");
    assert_eq!(
        format_units("2100000000000000", 8).unwrap(),
        "21000000.00000000"
    );
    assert_eq!(format_units("0", 8).unwrap(), "0.00000000");
    assert_eq!(format_units("0000123", 2).unwrap(), "1.23");
}

#[test]
fn format_units_survives_a_256_bit_wei_value() {
    let max = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
    assert_eq!(
        format_units(max, 18).unwrap(),
        "115792089237316195423570985008687907853269984665640564039457.584007913129639935"
    );
    assert_eq!(format_units("1", 18).unwrap(), "0.000000000000000001");
}

#[test]
fn format_units_handles_zero_decimals_and_negatives() {
    assert_eq!(format_units("42", 0).unwrap(), "42");
    assert_eq!(format_units("-150", 2).unwrap(), "-1.50");
    assert_eq!(format_units("-0", 2).unwrap(), "0.00");
}

#[test]
fn format_units_rejects_non_integers() {
    for bad in ["", "1.5", "abc", "1e5", "--1", " ", "0x10"] {
        assert!(format_units(bad, 8).is_err(), "{bad:?}");
    }
    assert!(format_units("1", 101).is_err());
}

#[test]
fn parse_units_is_the_exact_inverse() {
    assert_eq!(parse_units("0.001", 8).unwrap(), "100000");
    assert_eq!(parse_units("25", 6).unwrap(), "25000000");
    assert_eq!(parse_units("0.05", 18).unwrap(), "50000000000000000");
    for (base, d) in [
        ("123456", 8),
        ("1", 18),
        ("999999999999999999999999", 18),
        ("7", 0),
    ] {
        assert_eq!(
            parse_units(&format_units(base, d).unwrap(), d).unwrap(),
            base
        );
    }
}

#[test]
fn parse_units_accepts_unambiguous_shorthand() {
    assert_eq!(parse_units(".5", 2).unwrap(), "50");
    assert_eq!(parse_units("5.", 2).unwrap(), "500");
    assert_eq!(parse_units(" 1.10 ", 2).unwrap(), "110");
    assert_eq!(parse_units("-1.5", 2).unwrap(), "-150");
    assert_eq!(parse_units("0.00", 2).unwrap(), "0");
    assert_eq!(parse_units("-0", 2).unwrap(), "0");
}

#[test]
fn parse_units_refuses_to_round() {
    let err = parse_units("0.123456789", 8).unwrap_err();
    assert!(err.to_string().contains("refusing to round"));
    assert!(parse_units("0.1234567", 6).is_err());
}

#[test]
fn parse_units_rejects_junk() {
    for bad in ["", ".", "-", "1.2.3", "abc", "1,5", "1e8", "+1"] {
        assert!(parse_units(bad, 8).is_err(), "{bad:?}");
    }
}

#[test]
fn compare_amounts_is_exact_past_2_pow_53() {
    assert_eq!(
        compare_amounts("9007199254740993", "9007199254740992").unwrap(),
        Ordering::Greater
    );
    assert_eq!(
        compare_amounts("2100000000000000000000001", "2100000000000000000000000").unwrap(),
        Ordering::Greater
    );
    assert_eq!(compare_amounts("00100", "100").unwrap(), Ordering::Equal);
    assert_eq!(compare_amounts("99", "100").unwrap(), Ordering::Less);
    assert_eq!(compare_amounts("-5", "3").unwrap(), Ordering::Less);
    assert_eq!(compare_amounts("-5", "-30").unwrap(), Ordering::Greater);
    assert_eq!(compare_amounts("-0", "0").unwrap(), Ordering::Equal);
    assert!(compare_amounts("1.0", "1").is_err());
}

#[test]
fn a_full_payment_check_reads_naturally() {
    let (received, expected) = ("100000", "100000");
    assert!(compare_amounts(received, expected).unwrap().is_ge());
}

#[test]
fn to_u128_and_validation() {
    assert_eq!(to_u128("25000000").unwrap(), 25_000_000);
    assert!(to_u128("-1").is_err());
    assert!(to_u128(&"9".repeat(40)).is_err());
    assert!(is_base_units("0"));
    assert!(is_base_units("-12"));
    assert!(!is_base_units("1.0"));
}
