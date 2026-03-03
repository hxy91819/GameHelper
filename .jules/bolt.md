## 2025-02-13 - [LINQ vs Single-Pass Loop Optimization]
**Learning:** Found an opportunity to replace multiple LINQ queries (`Sum` and `Where().Sum()`) iterating over the same collection with a single-pass `foreach` loop in `GameHelper.Core/Services/StatisticsService.cs`. This specific architecture was performing O(2n) operations where O(n) would suffice, and reducing LINQ overhead in this statistics calculation hot-path provides a cleaner execution with fewer allocations.
**Action:** When calculating multiple aggregates or filtered aggregates from the same collection, prefer a single `foreach` pass over chaining multiple LINQ operations to reduce both iterations and memory allocations.

## 2025-02-13 - [LINQ Sum semantics: Checked vs Unchecked]
**Learning:** LINQ's `Sum()` method performs additions in a `checked` context (throwing an OverflowException if the value overflows), whereas standard C# arithmetic operators like `+=` in a `foreach` loop execute in an `unchecked` context by default unless specified otherwise.
**Action:** When replacing LINQ `Sum()` with standard loops for optimization, evaluate if overflow behavior needs to be preserved. If so, wrap the accumulation logic in a `checked { ... }` block to ensure consistent fast-fail behavior.
