package receivify

import "context"

// Iter walks every item of a paginated list, fetching pages lazily as it goes.
//
//	it := client.ListInvoicesAutoPaging(ctx, nil)
//	for it.Next() {
//		inv := it.Current()
//		_ = inv
//	}
//	if err := it.Err(); err != nil {
//		// handle
//	}
//
// An Iter is not safe for concurrent use.
type Iter[T any] struct {
	ctx    context.Context
	fetch  func(ctx context.Context, cursor string) (*List[T], error)
	cursor string
	page   []T
	idx    int
	cur    T
	more   bool
	start  bool
	err    error
	seen   map[string]struct{}
}

func newIter[T any](ctx context.Context, cursor string, fetch func(context.Context, string) (*List[T], error)) *Iter[T] {
	if ctx == nil {
		ctx = context.Background()
	}
	return &Iter[T]{ctx: ctx, fetch: fetch, cursor: cursor, seen: map[string]struct{}{}}
}

// Next advances to the next item, fetching the next page when needed. It returns false when
// the list is exhausted or an error occurred; check Err afterwards.
func (it *Iter[T]) Next() bool {
	for it.idx >= len(it.page) {
		if it.err != nil || (it.start && !it.more) {
			return false
		}
		if err := it.ctx.Err(); err != nil {
			it.err = err
			return false
		}
		page, err := it.fetch(it.ctx, it.cursor)
		if err != nil {
			it.err = err
			return false
		}
		it.start = true
		it.page, it.idx, it.more = page.Data, 0, page.HasMore
		if it.more {
			// has_more with no cursor would spin forever; a repeated cursor would loop.
			if page.NextCursor == "" {
				it.err = ErrPaginationNoCursor
				it.more = false
			} else if _, dup := it.seen[page.NextCursor]; dup {
				it.err = ErrPaginationLoop
				it.more = false
			} else {
				it.seen[page.NextCursor] = struct{}{}
				it.cursor = page.NextCursor
			}
		}
	}
	it.cur = it.page[it.idx]
	it.idx++
	return true
}

// Current returns the item Next just advanced to.
func (it *Iter[T]) Current() T { return it.cur }

// Err returns the first error encountered, or nil when the list was walked to the end.
func (it *Iter[T]) Err() error { return it.err }

// Collect drains the iterator into a slice, stopping after max items (max <= 0 means no
// limit). Be mindful of unbounded result sets.
func (it *Iter[T]) Collect(max int) ([]T, error) {
	var out []T
	for (max <= 0 || len(out) < max) && it.Next() {
		out = append(out, it.Current())
	}
	return out, it.Err()
}
