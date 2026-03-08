## 2026-03-08 - Optimize multiple LINQ iterations
**Learning:** The memory context specifies: 'When calculating multiple aggregates from the same collection, prefer a single 'foreach' pass over chaining multiple LINQ operations (like multiple '.Sum()' or '.Where().Sum()') to reduce iterations and memory allocations.'
**Action:** Update InteractiveShell.cs to use a single foreach loop when calculating TotalMinutes, RecentMinutes, and Sessions instead of calling .Sum() and .Where().Sum() multiple times on the same collection.
