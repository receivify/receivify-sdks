# frozen_string_literal: true

module Receivify
  # Internal decoding helpers. Not part of the public API.
  module Util
    module_function

    # Deep-copy a decoded JSON value with string keys, freezing every level.
    def deep_freeze(value)
      case value
      when Hash
        value.each_with_object({}) { |(k, v), h| h[k.to_s.freeze] = deep_freeze(v) }.freeze
      when Array
        value.map { |v| deep_freeze(v) }.freeze
      when String
        value.frozen? ? value : value.dup.freeze
      else
        value
      end
    end

    # Deep-copy a frozen structure into a mutable one.
    def deep_dup(value)
      case value
      when Hash then value.each_with_object({}) { |(k, v), h| h[k.dup] = deep_dup(v) }
      when Array then value.map { |v| deep_dup(v) }
      when String then value.dup
      else value
      end
    end

    def str(data, key, default = "")
      value = data[key]
      value.is_a?(String) ? value : default
    end

    def opt_str(data, key)
      value = data[key]
      value.is_a?(String) ? value : nil
    end

    def int(data, key, default = 0)
      value = data[key]
      case value
      when Integer then value
      when String then value.strip =~ /\A-?\d+\z/ ? value.strip.to_i : default
      else default
      end
    end

    def opt_int(data, key)
      value = data[key]
      value.is_a?(Integer) ? value : nil
    end

    def bool(data, key, default = false)
      value = data[key]
      value == true || value == false ? value : default
    end

    def obj(data, key)
      value = data[key]
      value.is_a?(Hash) ? value : {}.freeze
    end

    # A required money field, always a String, byte-for-byte as sent.
    def money(data, key, default = "0")
      value = opt_money(data, key)
      value.nil? ? default : value
    end

    # A nullable money field. The API types every amount as a JSON string precisely so no
    # client has to make this choice; if a JSON number ever arrives anyway it is stringified
    # from its shortest round-trip form, never through further float arithmetic.
    def opt_money(data, key)
      value = data[key]
      case value
      when nil, true, false then nil
      when String then value
      when Integer then value.to_s
      when Float then float_to_plain(value)
      else value.to_s
      end
    end

    # 1.0e-05 -> "0.00001", 2.5e+20 -> "250000000000000000000", 0.3 -> "0.3"
    def float_to_plain(value)
      text = value.to_s
      return text.sub(/\.0\z/, "") unless text.include?("e")

      mantissa, exp = text.split("e")
      exp = exp.to_i
      sign = mantissa.start_with?("-") ? "-" : ""
      mantissa = mantissa.delete("-")
      whole, frac = mantissa.split(".")
      frac = "" if frac.nil? || frac == "0"
      digits = whole + frac
      point = whole.length + exp
      out = if point <= 0
              "0." + ("0" * -point) + digits
            elsif point >= digits.length
              digits + ("0" * (point - digits.length))
            else
              digits[0, point] + "." + digits[point..-1]
            end
      out = out.sub(/\A0+(?=\d)/, "")
      out = out.sub(/(\.\d*?)0+\z/, '\1').sub(/\.\z/, "") if out.include?(".")
      sign + out
    end

    # Percent-encode one path segment, so an id can never inject a path.
    def segment(value)
      text = value.to_s
      raise ArgumentError, "An id is required." if text.empty?

      text.b.gsub(/[^A-Za-z0-9\-._~]/) { |c| format("%%%02X", c.ord) }
    end
  end

  # Base class for every response object.
  #
  # A resource is a small immutable view over the decoded JSON:
  #
  #   invoice.checkout_url        # typed reader
  #   invoice["checkout_url"]     # the raw value, exactly as the API sent it
  #   invoice[:checkout_url]      # symbols work too
  #   invoice.raw                 # the whole frozen Hash, including fields added after this release
  #   invoice.to_h                # a mutable deep copy of the raw Hash
  #
  # Three rules:
  # 1. Money never becomes a number. Every amount is a String.
  # 2. Unknown fields are never lost: they stay readable through #[] and #raw.
  # 3. Missing fields never raise: every reader has a default.
  class Resource
    Field = Struct.new(:name, :key, :type)

    class << self
      def fields
        @fields ||= {}
      end

      def inherited(subclass)
        super
        subclass.instance_variable_set(:@fields, fields.dup)
      end

      # Declare a typed reader. `type` is one of :str, :opt_str, :int, :opt_int, :bool,
      # :bool_true (bool defaulting to true), :money, :opt_money, :hash.
      def field(name, type = :str, key: nil)
        fields[name.to_sym] = Field.new(name.to_sym, (key || name).to_s, type)
        define_method(name) { @values[name.to_sym] }
      end

      # Build from a decoded JSON Hash. Anything that is not a Hash yields an empty resource.
      def from_hash(hash)
        new(hash)
      end
    end

    # The decoded body, deep-frozen, with String keys.
    attr_reader :raw

    def initialize(raw = {})
      @raw = Util.deep_freeze(raw.is_a?(Hash) ? raw : {})
      @values = {}
      self.class.fields.each_value { |f| @values[f.name] = decode(f) }
      decode_extra(@raw, @values)
      @values.freeze
      freeze
    end

    # Raw access by String or Symbol key: `invoice["status"]`, `invoice[:status]`.
    def [](key)
      @raw[key.to_s]
    end

    def key?(key)
      @raw.key?(key.to_s)
    end
    alias has_key? key?

    def dig(*keys)
      @raw.dig(*keys.map { |k| k.is_a?(Symbol) ? k.to_s : k })
    end

    def fetch(key, *default, &block)
      @raw.fetch(key.to_s, *default, &block)
    end

    def keys
      @raw.keys
    end

    # A mutable deep copy of the raw Hash.
    def to_h
      Util.deep_dup(@raw)
    end
    alias to_hash to_h

    def to_json(*args)
      @raw.to_json(*args)
    end

    def ==(other)
      other.class == self.class && other.raw == raw
    end
    alias eql? ==

    def hash
      [self.class, raw].hash
    end

    def inspect
      shown = @values.map do |k, v|
        v = "<redacted>" if self.class.redacted_fields.include?(k) && !v.nil?
        "#{k}=#{v.inspect}"
      end
      "#<#{self.class.name} #{shown.join(' ')}>"
    end
    alias to_s inspect

    def pretty_print(q)
      q.text(inspect)
    end

    def self.redacted_fields
      []
    end

    private

    def decode(field)
      case field.type
      when :str then Util.str(@raw, field.key)
      when :opt_str then Util.opt_str(@raw, field.key)
      when :int then Util.int(@raw, field.key)
      when :opt_int then Util.opt_int(@raw, field.key)
      when :bool then Util.bool(@raw, field.key)
      when :bool_true then Util.bool(@raw, field.key, true)
      when :money then Util.money(@raw, field.key)
      when :opt_money then Util.opt_money(@raw, field.key)
      when :hash then Util.obj(@raw, field.key)
      else raise ArgumentError, "unknown field type #{field.type}"
      end
    end

    # Hook for subclasses that decode nested collections.
    def decode_extra(_raw, _values); end
  end
end
