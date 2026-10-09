package receivify

import (
	"fmt"
	"math/big"
	"strings"
)

// Money helpers. The SDK never calls these for you: every amount stays exactly the string the
// API sent, because float64 cannot hold a satoshi count above 2^53, let alone a wei value.
// Convert only where you display, and only with these math/big-backed functions.
//
// decimals comes from Invoice.Decimals or Ticker.Decimals.

const maxDecimals = 100

func isInteger(s string) bool {
	if strings.HasPrefix(s, "-") {
		s = s[1:]
	}
	if s == "" {
		return false
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}

func checkDecimals(fn string, decimals int) error {
	if decimals < 0 || decimals > maxDecimals {
		return fmt.Errorf("receivify: %s: decimals must be in 0..%d, got %d", fn, maxDecimals, decimals)
	}
	return nil
}

// ToBigInt parses an exact-integer base-unit string. It is the safe way to do arithmetic on
// an amount.
func ToBigInt(base string) (*big.Int, error) {
	s := strings.TrimSpace(base)
	if !isInteger(s) {
		return nil, fmt.Errorf("receivify: expected an integer base-unit string, got %q", base)
	}
	n, ok := new(big.Int).SetString(s, 10)
	if !ok {
		return nil, fmt.Errorf("receivify: expected an integer base-unit string, got %q", base)
	}
	return n, nil
}

// FormatUnits converts exact base units to a display decimal string.
//
//	FormatUnits("123456", 8) // "0.00123456"
func FormatUnits(base string, decimals int) (string, error) {
	if err := checkDecimals("FormatUnits", decimals); err != nil {
		return "", err
	}
	n, err := ToBigInt(base)
	if err != nil {
		return "", err
	}
	neg := n.Sign() < 0
	digits := new(big.Int).Abs(n).String()
	if decimals == 0 {
		if neg {
			return "-" + digits, nil
		}
		return digits, nil
	}
	if len(digits) <= decimals {
		digits = strings.Repeat("0", decimals-len(digits)+1) + digits
	}
	whole, frac := digits[:len(digits)-decimals], digits[len(digits)-decimals:]
	out := whole + "." + frac
	if neg {
		out = "-" + out
	}
	return out, nil
}

// ParseUnits converts a display decimal string to exact base units. It returns an error rather
// than rounding when value has more fraction digits than the asset supports.
//
//	ParseUnits("0.00123456", 8) // "123456"
func ParseUnits(value string, decimals int) (string, error) {
	if err := checkDecimals("ParseUnits", decimals); err != nil {
		return "", err
	}
	s := strings.TrimSpace(value)
	neg := strings.HasPrefix(s, "-")
	body := strings.TrimPrefix(s, "-")
	whole, frac, _ := strings.Cut(body, ".")
	if body == "" || body == "." || (whole != "" && !isInteger(whole)) || (frac != "" && !isInteger(frac)) ||
		strings.HasPrefix(whole, "-") || strings.HasPrefix(frac, "-") || strings.Count(body, ".") > 1 {
		return "", fmt.Errorf("receivify: ParseUnits: expected a decimal string, got %q", value)
	}
	if len(frac) > decimals {
		return "", fmt.Errorf("receivify: ParseUnits: %q has %d fraction digits but the asset has %d; refusing to round a monetary value", value, len(frac), decimals)
	}
	if whole == "" {
		whole = "0"
	}
	n, ok := new(big.Int).SetString(whole+frac+strings.Repeat("0", decimals-len(frac)), 10)
	if !ok {
		return "", fmt.Errorf("receivify: ParseUnits: expected a decimal string, got %q", value)
	}
	if neg {
		n.Neg(n)
	}
	return n.String(), nil
}

// CompareAmounts compares two exact base-unit strings, returning -1, 0 or 1.
func CompareAmounts(a, b string) (int, error) {
	x, err := ToBigInt(a)
	if err != nil {
		return 0, err
	}
	y, err := ToBigInt(b)
	if err != nil {
		return 0, err
	}
	return x.Cmp(y), nil
}
