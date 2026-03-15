## 2026-03-15 - Optimize LINQ multiple passes to single foreach
**Learning:** When calculating multiple aggregates (e.g. TotalMinutes and RecentMinutes) from the same collection, chaining multiple LINQ operations (like `.Sum()` and `.Where().Sum()`) causes unnecessary iterations and memory allocations.
**Action:** Prefer a single `foreach` pass to calculate all required aggregates simultaneously, especially in critical paths.
