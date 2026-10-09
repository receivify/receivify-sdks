package receivify

import (
	"math/big"
	"testing"
)

func TestFormatUnits(t *testing.T) {
	cases := []struct {
		base string
		dec  int
		want string
	}{
		{"123456", 8, "0.00123456"},
		{"100000", 8, "0.00100000"},
		{"25000000", 6, "25.000000"},
		{"0", 8, "0.00000000"},
		{"1", 18, "0.000000000000000001"},
		{"2100000000000000000", 18, "2.100000000000000000"},
		{"115792089237316195423570985008687907853269984665640564039457584007913129639935", 18, "115792089237316195423570985008687907853269984665640564039457.584007913129639935"},
		{"42", 0, "42"},
		{"-150", 2, "-1.50"},
		{"-5", 0, "-5"},
		{"-0", 2, "0.00"},
		{"007", 2, "0.07"},
		{" 5 ", 1, "0.5"},
	}
	for _, c := range cases {
		got, err := FormatUnits(c.base, c.dec)
		if err != nil || got != c.want {
			t.Errorf("FormatUnits(%q,%d) = %q, %v; want %q", c.base, c.dec, got, err, c.want)
		}
	}
}

func TestFormatUnitsRejectsNonIntegers(t *testing.T) {
	for _, b := range []string{"", "1.5", "1e8", "abc", "+5", "-", "0x10", "1_000"} {
		if _, err := FormatUnits(b, 8); err == nil {
			t.Errorf("FormatUnits(%q) should fail", b)
		}
	}
}

func TestParseUnits(t *testing.T) {
	cases := []struct {
		v    string
		dec  int
		want string
	}{
		{"0.00123456", 8, "123456"},
		{"0.001", 8, "100000"},
		{"25", 6, "25000000"},
		{"0.05", 18, "50000000000000000"},
		{".5", 2, "50"},
		{"5.", 2, "500"},
		{"0", 8, "0"},
		{"-0", 8, "0"},
		{"-1.5", 2, "-150"},
		{"000.10", 2, "10"},
		{"7", 0, "7"},
	}
	for _, c := range cases {
		got, err := ParseUnits(c.v, c.dec)
		if err != nil || got != c.want {
			t.Errorf("ParseUnits(%q,%d) = %q, %v; want %q", c.v, c.dec, got, err, c.want)
		}
	}
}

func TestParseUnitsRefusesToRound(t *testing.T) {
	if _, err := ParseUnits("0.123456789", 8); err == nil {
		t.Fatal("9 fraction digits into an 8-decimal asset must error, not round")
	}
	if _, err := ParseUnits("1.5", 0); err == nil {
		t.Fatal("fraction into a 0-decimal asset must error")
	}
}

func TestParseUnitsRejectsJunk(t *testing.T) {
	for _, v := range []string{"", ".", "-", "abc", "1.2.3", "1e5", "--1", "1.-2", "+1", "1,5", " "} {
		if _, err := ParseUnits(v, 8); err == nil {
			t.Errorf("ParseUnits(%q) should fail", v)
		}
	}
}

func TestRoundTrip(t *testing.T) {
	for _, base := range []string{"1", "100000", "123456789012345678901234567890", "-42"} {
		for _, d := range []int{0, 6, 8, 18} {
			s, err := FormatUnits(base, d)
			if err != nil {
				t.Fatal(err)
			}
			back, err := ParseUnits(s, d)
			if err != nil || back != base {
				t.Errorf("round trip %s/%d -> %s -> %s (%v)", base, d, s, back, err)
			}
		}
	}
}

func TestDecimalsBounds(t *testing.T) {
	for _, d := range []int{-1, 101} {
		if _, err := FormatUnits("1", d); err == nil {
			t.Errorf("FormatUnits decimals %d should fail", d)
		}
		if _, err := ParseUnits("1", d); err == nil {
			t.Errorf("ParseUnits decimals %d should fail", d)
		}
	}
}

func TestCompareAmountsExactPast2to53(t *testing.T) {
	// float64(9007199254740993) == float64(9007199254740992): a float comparison ties.
	got, err := CompareAmounts("9007199254740993", "9007199254740992")
	if err != nil || got != 1 {
		t.Fatalf("got %d, %v", got, err)
	}
	if got, _ := CompareAmounts("5", "5"); got != 0 {
		t.Fatal("equal")
	}
	if got, _ := CompareAmounts("-1", "0"); got != -1 {
		t.Fatal("less")
	}
	if _, err := CompareAmounts("1.0", "1"); err == nil {
		t.Fatal("non-integer must fail")
	}
}

func TestToBigIntArithmetic(t *testing.T) {
	a, err := ToBigInt("25252526")
	if err != nil {
		t.Fatal(err)
	}
	b, _ := ToBigInt("25000000")
	if new(big.Int).Sub(a, b).String() != "252526" {
		t.Fatal("subtraction")
	}
	if _, err := ToBigInt("12.5"); err == nil {
		t.Fatal("decimal must fail")
	}
}

func TestFullPaymentCheckWithoutFloats(t *testing.T) {
	inv := Invoice{AmountExpected: "100000", AmountReceived: "100001"}
	cmp, err := CompareAmounts(inv.AmountReceived, inv.AmountExpected)
	if err != nil || cmp < 0 {
		t.Fatal("overpayment should count as paid in full")
	}
}
