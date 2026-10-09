# frozen_string_literal: true

require_relative "errors"
require_relative "resource"

module Receivify
  # One page of a list response: the API's one and only list envelope,
  # `{data, has_more, next_cursor}`.
  #
  #   page = client.list_invoices(limit: 50)
  #   page.each { |invoice| ... }     # Enumerable over #data
  #   page.has_more?                  # a fact from the server -- never infer it from the size
  #   page.next_cursor                # pass as cursor: to fetch the next page
  #
  # To walk EVERY page, use the matching each_* method on the client (client.each_invoice),
  # which follows next_cursor for you.
  class Page
    include Enumerable

    attr_reader :data, :next_cursor, :raw

    def initialize(payload, &item_factory)
      raise Receivify::Error, "Expected a list envelope object, got #{payload.class}." unless payload.is_a?(Hash)

      items = payload["data"].is_a?(Array) ? payload["data"] : []
      factory = item_factory || ->(h) { h }
      @data = items.select { |i| i.is_a?(Hash) }.map { |i| factory.call(i) }.freeze
      @has_more = payload["has_more"] == true
      cursor = payload["next_cursor"]
      @next_cursor = cursor.is_a?(String) && !cursor.empty? ? cursor.dup.freeze : nil
      @raw = Util.deep_freeze(payload)
      freeze
    end

    def has_more?
      @has_more
    end

    def has_more
      @has_more
    end

    def each(&block)
      return enum_for(:each) { size } unless block

      @data.each(&block)
      self
    end

    def size
      @data.size
    end
    alias length size

    def empty?
      @data.empty?
    end

    # page[0] is the first item; page["has_more"] reads the raw envelope.
    def [](key)
      key.is_a?(Integer) ? @data[key] : @raw[key.to_s]
    end

    def last
      @data.last
    end

    def to_a
      @data.dup
    end

    def inspect
      "#<Receivify::Page size=#{size} has_more=#{@has_more} next_cursor=#{@next_cursor.inspect}>"
    end

    def pretty_print(q)
      q.text(inspect)
    end
  end

  # Auto-pagination. Not part of the public API; use client.each_* instead.
  module Pagination
    module_function

    # Returns a lazy Enumerator over every item across every page. `fetch` receives the
    # cursor (nil first) and must return a Page. One request per page, made only when the
    # previous page is exhausted.
    #
    # Guards: has_more with no next_cursor ends the walk, and a cursor the server already
    # handed back raises instead of looping forever.
    def enumerate(start_cursor = nil, &fetch)
      Enumerator.new do |yielder|
        cursor = start_cursor
        seen = {}
        loop do
          page = fetch.call(cursor)
          page.data.each { |item| yielder << item }
          break if !page.has_more? || page.next_cursor.nil?
          if seen[page.next_cursor]
            raise Receivify::Error, "Pagination stalled: the server returned cursor #{page.next_cursor.inspect} twice."
          end

          seen[page.next_cursor] = true
          cursor = page.next_cursor
        end
      end
    end
  end
end
