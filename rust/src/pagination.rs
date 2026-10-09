//! Auto-pagination.

use std::collections::{HashSet, VecDeque};
use std::fmt;

use crate::error::{Error, Result};
use crate::models::Page;

type FetchFn<'a, T> = Box<dyn FnMut(Option<&str>) -> Result<Page<T>> + 'a>;

/// An iterator that follows `next_cursor` until `has_more` is false, yielding each item.
///
/// Pages are fetched lazily: breaking out of the loop (or `take(n)`) stops further requests.
/// An error is yielded once and ends the iteration. A repeated cursor, or `has_more: true` with
/// no cursor, yields [`Error::Pagination`] instead of looping forever.
pub struct Paginator<'a, T> {
    fetch: FetchFn<'a, T>,
    buffer: VecDeque<T>,
    cursor: Option<String>,
    seen: HashSet<String>,
    done: bool,
    pending_error: Option<Error>,
}

impl<'a, T> Paginator<'a, T> {
    /// Build a paginator from a function that fetches one page for a given cursor
    /// (`None` for the first page). Use it for list endpoints this SDK does not wrap.
    pub fn new<F>(start_cursor: Option<String>, fetch: F) -> Self
    where
        F: FnMut(Option<&str>) -> Result<Page<T>> + 'a,
    {
        let seen = start_cursor.iter().cloned().collect();
        Paginator {
            fetch: Box::new(fetch),
            buffer: VecDeque::new(),
            cursor: start_cursor,
            seen,
            done: false,
            pending_error: None,
        }
    }
}

impl<T> fmt::Debug for Paginator<'_, T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Paginator")
            .field("buffered", &self.buffer.len())
            .field("cursor", &self.cursor)
            .field("done", &self.done)
            .finish()
    }
}

impl<T> Iterator for Paginator<'_, T> {
    type Item = Result<T>;

    fn next(&mut self) -> Option<Self::Item> {
        loop {
            if let Some(item) = self.buffer.pop_front() {
                return Some(Ok(item));
            }
            if self.done {
                return self.pending_error.take().map(Err);
            }
            let page = match (self.fetch)(self.cursor.as_deref()) {
                Ok(p) => p,
                Err(e) => {
                    self.done = true;
                    return Some(Err(e));
                }
            };
            self.buffer.extend(page.data);
            if !page.has_more {
                self.done = true;
                continue;
            }
            match page.next_cursor {
                Some(next) if !next.is_empty() => {
                    if self.seen.insert(next.clone()) {
                        self.cursor = Some(next);
                    } else {
                        // Hand out this page, then report the broken contract.
                        self.done = true;
                        self.pending_error = Some(Error::Pagination(format!(
                            "cursor {next:?} repeated; refusing to loop"
                        )));
                    }
                }
                _ => {
                    self.done = true;
                    self.pending_error = Some(Error::Pagination(
                        "has_more is true but next_cursor is empty; cannot continue".into(),
                    ));
                }
            }
        }
    }
}
