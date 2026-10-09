# frozen_string_literal: true

require_relative "test_helper"

class MoneyTest < Minitest::Test
  M = Receivify::Money

  def test_format_amount
    assert_equal "0.00042000", M.format_amount("42000", 8)
    assert_equal "0.00042", M.format_amount("42000", 8, trim: true)
    assert_equal "25.000000", M.format_amount("25000000", 6)
    assert_equal "25", M.format_amount("25000000", 6, trim: true)
    assert_equal "1.000000000000000001", M.format_amount("1000000000000000001", 18)
    assert_equal "0", M.format_amount("0", 0)
    assert_equal "0.00", M.format_amount("0", 2)
    assert_equal "-1.50", M.format_amount("-150", 2)
    assert_equal "42", M.format_amount(42, 0)
  end

  def test_format_amount_is_exact_for_huge_values
    wei = "115792089237316195423570985008687907853269984665640564039457584007913129639935"
    out = M.format_amount(wei, 18)
    assert_equal wei, out.delete(".")
  end

  def test_format_amount_rejects_decimals_input
    assert_raises(ArgumentError) { M.format_amount("25.00", 2) }
    assert_raises(ArgumentError) { M.format_amount("abc", 2) }
    assert_raises(ArgumentError) { M.format_amount("1", -1) }
  end

  def test_to_base_units
    assert_equal "42000", M.to_base_units("0.00042", 8)
    assert_equal "100000", M.to_base_units("0.001", 8)
    assert_equal "25000000", M.to_base_units("25", 6)
    assert_equal "50000000000000000", M.to_base_units("0.05", 18)
    assert_equal "0", M.to_base_units("0", 8)
    assert_equal "1", M.to_base_units(".00000001", 8)
    assert_equal "-150", M.to_base_units("-1.5", 2)
    assert_equal "1000", M.to_base_units(10, 2)
    assert_equal "120", M.to_base_units("1.2000", 2)
  end

  def test_to_base_units_refuses_to_lose_money
    assert_raises(ArgumentError) { M.to_base_units("0.000000001", 8) }
    assert_raises(ArgumentError) { M.to_base_units("1.2.3", 8) }
    assert_raises(ArgumentError) { M.to_base_units("1e5", 8) }
  end

  def test_to_base_units_refuses_floats
    assert_raises(TypeError) { M.to_base_units(0.1, 8) }
  end

  def test_bigdecimal_helpers
    require "bigdecimal"
    assert_equal BigDecimal("0.00042"), M.to_decimal("42000", 8)
    assert_equal BigDecimal("1.000000000000000001"), M.to_decimal("1000000000000000001", 18)
    assert_equal "42000", M.to_base_units(BigDecimal("0.00042"), 8)
  end

  def test_round_trip
    %w[0 1 99 100000 2100000000000000 1000000000000000001].each do |units|
      assert_equal units, M.to_base_units(M.format_amount(units, 8), 8)
    end
  end
end
