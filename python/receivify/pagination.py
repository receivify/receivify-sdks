"""The one and only list envelope, plus the auto-paginating generator.

Every list endpoint in the API returns exactly ``{data, has_more, next_cursor}``; no
endpoint returns a bare array.  ``has_more`` is a fact (the server reads ``limit + 1``
rows) -- never infer it from ``len(page) == limit``.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Dict, Generic, Iterator, List, Mapping, Optional, TypeVar

from .errors import ReceivifyError

__all__ = ["Page", "auto_paginate"]

T = TypeVar("T")


@dataclass(frozen=True)
class Page(Generic[T]):
    """One page of a list response.

    Iterating a page iterates :attr:`data`.  To walk *every* page, use the matching
    ``iter_*`` generator on the client (``client.iter_invoices()``), which follows
    :attr:`next_cursor` for you.
    """

    data: List[T] = field(default_factory=list)
    has_more: bool = False
    next_cursor: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    def __iter__(self) -> Iterator[T]:
        return iter(self.data)

    def __len__(self) -> int:
        return len(self.data)

    def __getitem__(self, index: int) -> T:
        return self.data[index]

    @classmethod
    def from_dict(cls, payload: Any, item_factory: Callable[[Mapping[str, Any]], T]) -> "Page[T]":
        if not isinstance(payload, Mapping):
            raise ReceivifyError("Expected a list envelope object, got {}.".format(type(payload).__name__))
        raw_items = payload.get("data")
        items: List[T] = []
        if isinstance(raw_items, list):
            items = [item_factory(item) for item in raw_items if isinstance(item, Mapping)]
        cursor = payload.get("next_cursor")
        return cls(
            data=items,
            has_more=bool(payload.get("has_more")),
            next_cursor=cursor if isinstance(cursor, str) and cursor else None,
            raw=dict(payload),
        )


def auto_paginate(fetch: Callable[..., Page[T]], **params: Any) -> Iterator[T]:
    """Yield every item across every page, following ``next_cursor``.

    ``fetch`` is any callable taking a ``cursor=`` keyword and returning a :class:`Page`.
    Lazy: one HTTP request per page, made only when the previous page is exhausted.

    Two guards, because an auto-paginator that loops forever is worse than one that stops:
    a ``has_more`` with no ``next_cursor`` ends the walk, and a cursor the server has
    already handed us raises rather than spinning.
    """
    cursor: Optional[str] = params.pop("cursor", None)
    seen: Dict[str, bool] = {}
    while True:
        page = fetch(cursor=cursor, **params)
        for item in page.data:
            yield item
        if not page.has_more or not page.next_cursor:
            return
        if page.next_cursor in seen:
            raise ReceivifyError(
                "Pagination stalled: the server returned cursor {!r} twice.".format(page.next_cursor)
            )
        seen[page.next_cursor] = True
        cursor = page.next_cursor
