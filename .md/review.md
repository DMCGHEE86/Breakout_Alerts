# BreakoutAlerts Code Review

## Summary

This repository has a strong, well-structured architecture for a WPF-based equity scanner and alert system. The core `BreakoutAlerts.Core` project is cleanly separated from the WPF `BreakoutAlerts.App` host, and the scanning, strategy, alerting, and persistence layers are implemented with careful attention to thread safety and operational reliability.

## What is working well

- **Clear separation of concerns**
  - `ScannerEngine` is a plain core class with a single public `RunCycleAsync`, while `ScannerHostedService` owns timing and application lifetime.
  - The strategy contract in `IPriceStrategy` and the runtime `IStrategyRegistry` keep the scanner decoupled from any specific strategy implementation.

- **Strong strategy design**
  - `OpeningRangeBreakoutStrategy` keeps mutable state keyed per ticker, which is essential for multi-symbol scanning.
  - The strategy uses bar deduplication, session resets, and explicit invalidation logic, avoiding repeated alerts and stale state.
  - The domain model (`Bar`, `StrategySignal`, `StrategyParameterDescriptor`) is well documented and uses `decimal` for prices, which is correct for financial logic.

- **Good persistence and logging practices**
  - `JsonLinesAlertStore` is a good choice for an append-only alert history. The file format is resilient and the implementation is careful about write serialization and malformed line handling.
  - `JsonWatchlistService` uses atomic save via a temp file and replace, which is appropriate for a small, user-edited file.
  - The `AlertNotificationService` persists before publishing and isolates subscriber failures so one bad UI handler cannot kill the scan.

- **Robust WPF host architecture**
  - The app uses the generic host, DI, and `IHostedService` so background services start/stop cleanly.
  - There is a global single-instance mutex and a user data directory in `LocalApplicationData`, which is correct for desktop tools.
  - UI event handlers correctly marshal background-thread events onto the dispatcher.

- **Thoughtful comments and design notes**
  - The codebase contains many well-written comments that explain why decisions were made, not just what the code does.
  - Live gateway quirks and the rationale for design choices are recorded inline, which is valuable for future maintenance.

## Risks and concerns

- **Limited test coverage outside ORB strategy**
  - The repository has good coverage for the ORB strategy behavior, but there appear to be fewer tests for:
    - `ScannerEngine` behavior and symbol-level failure containment
    - `JsonLinesAlertStore` duplicate suppression and legacy migration
    - `JsonWatchlistService` load/save semantics
    - WPF view model alert ingestion and synchronization

- **Potential edge-case with `today` filtering**
  - `ScannerEngine` filters alerts based on `Strategies.MarketSession.SessionDate(DateTimeOffset.Now)`. If the host machine clock is in a different timezone or the app runs near midnight ET, signals may be misclassified as not today. Using the exchange-relative timestamp or provider-derived date would reduce this risk.

- **Event thread context dependency**
  - `IAlertNotificationService` raises `AlertRaised` on the publishing thread. The UI handles this correctly today, but any future subscriber must remember to marshal to the UI thread. This is documented, but it remains a sharp edge.

- **Potential UI backlog from frequent alerts**
  - `DashboardViewModel.OnAlertRaised` uses `Dispatcher.BeginInvoke` to enqueue each alert update. If alert volume spikes, the UI may lag behind the incoming stream. This is acceptable for moderate load, but high-frequency scenarios could benefit from a bounded or batching update strategy.

- **`StrategyRegistry.Active` snapshot cost**
  - `Active` returns a new list every call. It is probably fine for the scan cycle volume here, but if the watchlist or strategy count grows significantly, this could become a small allocation hotspot.

## Specific recommendations

1. **Add tests for core runtime flows**
   - Add unit/integration tests for `ScannerEngine` to verify:
     - active strategies are evaluated per symbol
     - a single symbol failure does not abort the cycle
     - signal cutoff and today-filter suppression
   - Add tests for `JsonLinesAlertStore` to verify duplicate suppression and malformed-line resilience.
   - Add coverage for `JsonWatchlistService` load, save, and add/remove logic.

2. **Use exchange-relative date logic for today filtering**
   - Replace `DateTimeOffset.Now` in `ScannerEngine` with a provider of exchange-relative current time or `TriggeredAt`-based session date determination.
   - This will avoid misclassifying alerts when running outside US Eastern time or around session boundaries.

3. **Keep event threading explicit for new subscribers**
   - Consider adding a helper or wrapper for UI consumers that enforces dispatcher marshalling, so new subscribers do not repeat the same pattern.
   - Alternatively, document this pattern in the `IAlertNotificationService` interface summary more prominently.

4. **Consider a stronger `IAsyncDisposable` cleanup path**
   - `JsonLinesAlertStore` and `JsonWatchlistService` have managed resources and could benefit from being registered as disposable services explicitly, although current lifetime is application-long and this is low priority.

5. **Review the `.md` documentation conventions**
   - The existing `.md` folder uses `.txt` file extensions for markdown content. Adding `review.md` is fine, but consider normalizing the folder to `.md` files for readability and tooling compatibility.

## Suggested next steps

- Create a small test suite for `ScannerEngine` and `JsonLinesAlertStore` first, since they are the core runtime contract and persistence boundary.
- Audit other strategy implementations once more than one strategy exists, to ensure `AlertRecord.Levels` remains a flexible schema and strategy-specific contexts do not leak.
- If real-time performance becomes important, add a bounded queue or batch consumer for alert insertion in `DashboardViewModel`.

## Overall impression

This is a well-designed codebase with a strong architectural foundation. The core domain and persistence layers are implemented carefully, and the WPF host wiring is solid. The highest-value improvements are better runtime coverage around the scan engine and alert store, plus a small hardening of the today/filtering logic to be exchange-time aware.
