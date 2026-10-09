// The list envelope and the auto-paginating iterator.
//
// Every list endpoint in this API returns exactly `{ data, has_more, next_cursor }` — no endpoint
// returns a bare array — so it is modelled once, as one template, and every list method reuses it.
//
// `has_more` is a FACT (the server reads `limit + 1` rows), never inferred from
// `data.size() == limit`. Do not re-derive it.
#pragma once

#include <cstddef>
#include <functional>
#include <iterator>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "receivify/errors.hpp"
#include "receivify/json.hpp"

namespace receivify {

/// One page of a list response.
template <typename T>
struct Page {
  std::vector<T> data;                      ///< The page, newest first.
  bool has_more = false;                    ///< A fact from the server.
  std::optional<std::string> next_cursor;   ///< Set exactly when `has_more` is true.
  Json raw;                                 ///< The whole envelope.

  static Page<T> from_json(const Json& j) {
    Page<T> page;
    const Json& items = j["data"];
    page.data.reserve(items.size());
    for (const Json& item : items.values()) page.data.push_back(T::from_json(item));
    page.has_more = j["has_more"].as_bool_or(false);
    if (j["next_cursor"].is_string() && !j["next_cursor"].as_string().empty()) {
      page.next_cursor = j["next_cursor"].as_string();
    }
    page.raw = j;
    return page;
  }
};

/// A lazy, forward-only sequence over every page of a list endpoint.
///
///     for (const receivify::Invoice& inv : client.invoices({/*status=*/"paid"})) {
///       std::cout << inv.id << ' ' << inv.amount_expected << '\n';
///     }
///
/// Pages are fetched on demand, so breaking out early stops the HTTP traffic. **Single pass** —
/// calling `begin()` twice does not restart the sequence; construct a fresh `Paginator` for that.
///
/// Two guards keep a malformed server from looping forever: `has_more: true` with no cursor, and
/// a cursor the sequence has already used, both throw `ReceivifyError`.
template <typename T>
class Paginator {
 private:
  struct State;

 public:
  /// Fetch one page starting at `cursor` (absent for the first page).
  using Fetch = std::function<Page<T>(const std::optional<std::string>& cursor)>;

  explicit Paginator(Fetch fetch, std::optional<std::string> start_cursor = std::nullopt)
      : state_(std::make_shared<State>()) {
    state_->fetch = std::move(fetch);
    state_->cursor = std::move(start_cursor);
  }

  class iterator {
   public:
    using iterator_category = std::input_iterator_tag;
    using value_type = T;
    using difference_type = std::ptrdiff_t;
    using pointer = const T*;
    using reference = const T&;

    iterator() = default;
    explicit iterator(std::shared_ptr<State> state) : state_(std::move(state)) {}

    reference operator*() const { return state_->buffer[state_->index]; }
    pointer operator->() const { return &state_->buffer[state_->index]; }

    iterator& operator++() {
      ++state_->index;
      state_->fill();
      if (state_->at_end()) state_.reset();
      return *this;
    }

    void operator++(int) { ++*this; }

    friend bool operator==(const iterator& a, const iterator& b) { return a.state_ == b.state_; }
    friend bool operator!=(const iterator& a, const iterator& b) { return !(a == b); }

   private:
    std::shared_ptr<State> state_;
  };

  iterator begin() {
    state_->fill();
    if (state_->at_end()) return iterator();
    return iterator(state_);
  }

  iterator end() { return iterator(); }

  /// Drain the sequence into a vector. Be deliberate about `max` — an unfiltered list can be very
  /// large, which is exactly why the iterator is the default shape.
  std::vector<T> collect(std::size_t max = static_cast<std::size_t>(-1)) {
    std::vector<T> out;
    for (auto it = begin(); it != end(); ++it) {
      out.push_back(*it);
      if (out.size() >= max) break;
    }
    return out;
  }

 private:
  struct State {
    Fetch fetch;
    std::optional<std::string> cursor;
    bool started = false;
    bool exhausted = false;
    std::vector<T> buffer;
    std::size_t index = 0;
    std::set<std::string> seen;

    bool at_end() const { return exhausted && index >= buffer.size(); }

    /// Make `buffer[index]` valid, fetching pages until there is an item or the list is done.
    void fill() {
      while (index >= buffer.size() && !exhausted) {
        Page<T> page = fetch(started ? cursor : std::optional<std::string>{});
        started = true;
        buffer = std::move(page.data);
        index = 0;

        if (!page.has_more) {
          exhausted = true;
        } else if (!page.next_cursor.has_value()) {
          // The API promises this cannot happen; if it ever did, paging would spin forever.
          throw ReceivifyError("Receivify: has_more is true but next_cursor is empty — cannot continue paging");
        } else if (!seen.insert(*page.next_cursor).second) {
          throw ReceivifyError("Receivify: pagination cursor repeated ('" + *page.next_cursor +
                               "') — refusing to loop");
        } else {
          cursor = page.next_cursor;
        }
      }
    }
  };

  std::shared_ptr<State> state_;
};

}  // namespace receivify
