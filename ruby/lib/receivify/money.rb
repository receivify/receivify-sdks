# frozen_string_literal: true

module Receivify
  # Opt-in helpers for base-unit amounts.
  #
  # Every amount the SDK hands back is a String and stays one; converting is a decision you
  # make per call site:
  #
  #   Receivify::Money.format_amount("42000", 8)              # => "0.00042000"
  #   Receivify::Money.format_amount("42000", 8, trim: true)  # => "0.00042"
  #   Receivify::Money.to_base_units("0.00042", 8)            # => "42000"
  #   Receivify::Money.to_decimal("42000", 8)                 # => BigDecimal("0.00042")
  #
  # Why not `amount.to_f / 10**decimals`? A wei value has up to 78 significant digits and a
  # Float about 15. These conversions move the decimal point in the digit string instead, so
  # they are exact at any magnitude. Only #to_decimal touches BigDecimal (loaded on demand).
  #
  # `decimals` comes from Invoice#decimals or Ticker#decimals.
  module Money
    module_function

    # Base units -> display-unit decimal String. Exact for any magnitude.
    def format_amount(base_units, decimals, trim: false)
      decimals = check_decimals(decimals)
      sign, digits = split_sign(base_units.to_s)
      unless digits =~ /\A\d+\z/
        raise ArgumentError,
              "Expected an exact-integer base-unit string, got #{base_units.inspect}. Decimal display " \
              "amounts (amount_fiat, amount_decimal, min_forward) are already display units."
      end
      digits = digits.sub(/\A0+(?=\d)/, "")
      text = if decimals.zero?
               digits
             else
               padded = digits.rjust(decimals + 1, "0")
               "#{padded[0...-decimals]}.#{padded[-decimals..-1]}"
             end
      text = text.sub(/0+\z/, "").sub(/\.\z/, "") if trim && text.include?(".")
      sign = "" if text =~ /\A[0.]+\z/
      sign + text
    end

    # Display-unit amount -> exact-integer base-unit String.
    #
    # Accepts a decimal String, an Integer or a BigDecimal. A Float is rejected: by the time
    # it gets here the value may already be wrong. Raises ArgumentError when the amount has
    # more precision than `decimals` allows -- rounding there would silently lose money.
    def to_base_units(amount, decimals)
      decimals = check_decimals(decimals)
      if amount.is_a?(Float)
        raise TypeError, "Refusing to convert a Float: pass a decimal String (or a BigDecimal) so the " \
                         "value is not already rounded before it gets here."
      end
      text = if defined?(::BigDecimal) && amount.is_a?(::BigDecimal)
               amount.to_s("F")
             else
               amount.to_s.strip
             end
      sign, rest = split_sign(text)
      whole, _, fraction = rest.partition(".")
      whole = "0" if whole.empty?
      unless whole =~ /\A\d+\z/ && (fraction.empty? || fraction =~ /\A\d+\z/)
        raise ArgumentError, "Expected a decimal amount, got #{amount.inspect}"
      end

      if fraction.length > decimals
        unless fraction[decimals..-1] =~ /\A0*\z/
          raise ArgumentError, "#{amount.inspect} has more precision than #{decimals} decimals can " \
                               "represent; rounding here would lose money."
        end
        fraction = fraction[0, decimals]
      end
      combined = (whole + fraction.ljust(decimals, "0")).sub(/\A0+/, "")
      combined = "0" if combined.empty?
      sign == "-" && combined != "0" ? "-#{combined}" : combined
    end

    # Base units -> BigDecimal in display units. Requires the bigdecimal library (a default
    # gem through Ruby 3.3; add `gem "bigdecimal"` to your Gemfile on 3.4+).
    def to_decimal(base_units, decimals)
      require "bigdecimal"
      BigDecimal(format_amount(base_units, decimals))
    end

    def split_sign(text)
      text = text.strip
      return ["-", text[1..-1]] if text.start_with?("-")
      return ["", text[1..-1]] if text.start_with?("+")

      ["", text]
    end

    def check_decimals(decimals)
      unless decimals.is_a?(Integer) && decimals >= 0
        raise ArgumentError, "decimals must be a non-negative Integer, got #{decimals.inspect}"
      end

      decimals
    end
  end
end
