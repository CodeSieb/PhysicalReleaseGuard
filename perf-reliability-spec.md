# Performance & Reliability Improvements — Spec

> Status: Draft v0.1 — distilled from an interview with the project owner.
> Target plugin version: **1.11.0.0** (bumps the minor; non-breaking).

---

## 1. Background & Motivation

`PhysicalReleaseGuard` currently issues one TMDb HTTP request per media item (search movie / search series / release-dates / episode-groups) inside a `foreach`/await loop. On large libraries (e.g. 5,000–20,000 items) this:

- Slower than needed — the scan is effectively serial.
- Polite-client behavior is absent — no rate limiting, no retry on 429/5xx, no exponential backoff.
- Silent on lookup misses — when TMDb doesn't return a match, only a single log line is emitted. Users have no aggregated view of *what* couldn't be matched and can't fix mislinked items without diving into logs.
- Failure modes are too rigid — if TMDb returns an error for one item, the scan stops behaving politely across the rest.
- Anime titles can be hard for TMDb alone to disambiguate, but adding a second metadata source is a much larger scope.

This spec bundles the speed/reliability improvements the owner has chosen, while keeping scope deliberate. **Caching is explicitly out of scope** (owner confirmed in Round 3).

---

## 2. Scope (decisions captured)

| Decision (from interview) | Selected |
|---|---|
| Focus area | Performance / reliability |
| Motivation | Performance at scale |
| Library scope | Movies, series, anime (no separate anime pipeline) |
| Size | Larger feature (multi-file, config-page additions, new endpoints) |
| Caching | **Skip** — rely on rate-limiting + retry |
| Concurrency | **Configurable** max-parallelism |
| Rate-limit budget | **User-configurable, conservative default** (4 req/sec) |
| "Tell the user when an item has no TMDb match" visibility | **Config-page banner / panel** |
| Failure-handling policy | **User-configurable** |
| Anime handling | **Whatever's the best** — keep current Series-path processing; rely on the new unmatched panel for manual fixes (no Mal/AniList/Kitsu integration in this spec) |

---

## 3. Goals

1. **Speed**: a 10k-item scan completes in a meaningful fraction of its current runtime, achieved via bounded concurrent requests + polite throttling.
2. **Politeness**: never exceed TMDb's tolerance — back off on 429/5xx, smooth bursts with a token-bucket, and respect an "after N consecutive failures, stop" circuit breaker if enabled.
3. **Observability**: when an item can't be resolved against TMDb, surface it persistently on the config page so an admin can fix it without log-diving.
4. **Manual override path**: provide a way for the admin to pin a TMDb ID for items the auto-search can't resolve, bypassing the search step on subsequent scans.
5. **Backward compatibility**: every existing config field keeps the same default and meaning; behavior on a fresh install is unchanged unless the user opts in via new settings.

---

## 4. Non-Goals

- **No response cache** (e.g. SQLite/JSON file of TMDb results). Per owner, this is explicitly out.
- **No second metadata source** (Trakt.tv, OMDb, AniDB, AniList, Kitsu, Mal). Anime is treated under the current Series-path pipeline.
- **No new tag semantics** (no "Coming Soon" / "Available" second tag, no theatrical vs physical distinction).
- **No UI redesign** beyond the new banner/panel and the new config inputs.
- **No new scheduled-task triggers or libraries.**

---

## 5. Architecture overview

```
                   ┌─────────────────────────────────────┐
                   │   Scan source-of-truth (existing)   │
                   │  HiddenTagScanTask                 │
                   │  PhysicalReleaseGuardController    │
                   │  LibraryWatcherService (auto-add) │
                   └─────────────────┬───────────────────┘
                                     │
                              bounded-parallel fan-out
                                     │
                                     ▼
                          ┌────────────────────┐
                          │  ResilientHttpClient│  ← wraps HttpClient
                          │  ├─ Token bucket    │
                          │  ├─ Retry policy    │
                          │  └─ Circuit breaker │
                          └─────────┬──────────┘
                                    │
                                    ▼
              ┌──────────────────────────────────┐
              │   TmdbService (refactored)       │
              │   - SearchMovieAsync             │
              │   - SearchSeriesAsync            │
              │   - HasPhysicalReleaseAsync      │
              │   - HasSeriesPhysicalReleaseAsync│
              │   - GetCountriesAsync            │
              └──────────────────────────────────┘

New side-channel:
                          ┌──────────────────────────┐
                          │ UnmatchedItemsStore      │
                          │  (JSON file under plugin │
                          │   config directory)      │
                          └─────────┬────────────────┘
                                    │ GET /UnmatchedItems
                                    │ POST /ClearUnmatchedItems
                                    │ POST /ManualLink  (itemId, tmdbId)
                                    │ POST /RetryLookup (itemId)
                                    ▼
                       Config page (new section)
```

---

## 6. New configuration

Add to `Configuration/PluginConfiguration.cs`:

```csharp
public int MaxRequestsPerSecond { get; set; } = 4;
public int MaxDegreeOfParallelism { get; set; } = 4;
public int MaxRetriesPerItem { get; set; } = 3;
public int RetryInitialDelayMs { get; set; } = 500;
public bool EnableCircuitBreaker { get; set; } = true;
public int CircuitBreakerThreshold { get; set; } = 10;
public bool TrackUnmatchedItems { get; set; } = true;
public int UnmatchedItemMaxEntries { get; set; } = 1000;

// Manual pin: ItemId (normalized) -> TMDb ID  (string)
public ManualTmdbLink[] ManualTmdbLinks { get; set; } = Array.Empty<ManualTmdbLink>();

public class ManualTmdbLink
{
    public string ItemId { get; set; } = string.Empty;
    public int TmdbId { get; set; }
}
```

Defaults are conservative so existing installs don't suddenly hammer TMDb. Owners of big libraries can crank up parallelism and rate-limit after they observe scans are working safely.

---

## 7. Sub-feature specs

### 7.1 Polite TMDb client (`ResilientHttpClient`)

- Single shared `HttpClient` instance (sockets stay open).
- **Token bucket**: per-second refilling, capped at `MaxRequestsPerSecond`. Refill is lazy (computed at request time from elapsed wall-clock).
- **Retry**: on `HttpRequestException`, `TaskCanceledException` (timeout — not user-cancel), or `429`, perform up to `MaxRetriesPerItem` retries with exponential backoff plus jitter (initial `RetryInitialDelayMs`, doubling each attempt; max delay of 30 s).
- **Circuit breaker**: if enabled, track consecutive failures across the scan. Hit `CircuitBreakerThreshold` → throw a `CircuitOpenException` that callers catch and translate into a logged + scanned "scanner aborted" message, without propagating into a crash.
- **Logging**: Debug on hit, Warning on each backoff + retry, Error on permanent fail + on circuit-open.

Files to add/modify:

- New: `Services/TokenBucket.cs`
- New: `Services/ResilientHttpClient.cs` (wraps `HttpClient`, exposes `Task<string> GetStringAsync(url, ct)`)
- Modify: `Services/TmdbService.cs` so all HTTP calls go through `ResilientHttpClient`. Each method becomes thinner: just build URL, deserialize JSON, surface results.

### 7.2 Bounded-parallel scans

- Replace the `foreach`/await loop in:
  - `Tasks/HiddenTagScanTask.ExecuteAsync` (the scheduled task)
  - `Api/PhysicalReleaseGuardController.ScanLibraryItemsAsync` (single-library scan)
  - `Services/LibraryWatcherService.ProcessItemAsync` (auto-scan: keep fire-and-forget, but throttle with a global semaphore so a flood of `ItemAdded` events doesn't open unbounded tasks)
- Use `SemaphoreSlim(MaxDegreeOfParallelism)` with `await WaitAsync` then `await Task` then `Release()`. Each task is independent — order of processing does not need to be preserved; logging already names each item.
- Progress reporting in `HiddenTagScanTask` is preserved by counting completions into `Interlocked.Increment(ref processed)`.

### 7.3 Unmatched-items panel

A new persistent JSON file under the plugin's data directory (e.g. `PhysicalReleaseGuard/unmatched.json`) records items the matcher gave up on. Schema:

```json
[
  {
    "ItemId": "abc123def...",
    "ItemName": "Some Movie",
    "ItemType": "Movie",
    "ProductionYear": 2025,
    "Reason": "NoSearchResults | ReleaseDataNull | HttpError",
    "Message": "TMDb returned no search match for 'Some Movie' (2025).",
    "FirstSeenUtc": "2026-06-22T01:23:45Z",
    "LastSeenUtc": "2026-06-22T01:23:45Z"
  }
]
```

- On every scan, when `SearchMovieAsync` / `SearchSeriesAsync` / `HasPhysicalReleaseAsync` / `HasSeriesPhysicalReleaseAsync` returns `null`, the helper service upserts the entry and bumps `LastSeenUtc`. Reasons captured:
  - `NoSearchResults` — TMDb search returned no/few candidates (current logging at Debug).
  - `ReleaseDataNull` — TMDb movie has no release-data endpoint result.
  - `HttpError` — non-retryable HTTP failure (4xx other than 429, JSON parse error). Retried-after-backoff failures do not increment to HttpError unless they exhaust retries.
- The list is capped at `UnmatchedItemMaxEntries` (oldest evicted). When user clicks "Clear list", the file is reset.
- A row's persistence is tied to the JDBC item still existing in the library — deleted/missing items are pruned when the entry is re-encountered.

New endpoints:
- `GET /PhysicalReleaseGuard/UnmatchedItems` — returns the array.
- `POST /PhysicalReleaseGuard/UnmatchedItems/Clear` — clears list.
- `POST /PhysicalReleaseGuard/UnmatchedItems/RetryLookup` — body `{ ItemId }`. Re-runs the matching flow for that single item and updates the entry on success (remove from list).

`HiddenTagService` calls into the store via a new injected `IUnmatchedItemsStore`. When `TrackUnmatchedItems=false`, the store becomes a no-op (no I/O, no file writes).

### 7.4 Manual TMDb-link panel

On the config page, in the "Unmatched TMDb Items" section, each row shows:
- Item name + type + year
- Reason
- A text input for a TMDb ID
- A "Save link" button (POST `/PhysicalReleaseGuard/UnmatchedItems/ManualLink` with `{ ItemId, TmdbId }`)

Server side:
- Endpoint validates the ID by hitting `GET /movie/{id}` or `GET /tv/{id}` (cheap call); if TMDb says "not found", return 400.
- Persist pair in `ManualTmdbLinks`. Use it in `HiddenTagService` ahead of any search: if a manual pin exists, use it and **bypass** auto search.

### 7.5 Config-page additions

Add to `Configuration/configPage.html`:
- A new section **"Performance & Reliability"**:
  - Max requests per second (number input, 1–50, default 4)
  - Max parallelism (number input, 1–32, default 4)
  - Max retries per item (number input, 0–10, default 3)
  - Retry initial delay (number input, ms, default 500)
  - Enable circuit-breaker (checkbox, default true)
  - Circuit-breaker threshold (number input, default 10)
  - Track unmatched items (checkbox, default true)
- A new section **"Unmatched TMDb Items"**:
  - Header with item count
  - "Refresh", "Clear all" buttons
  - Paginated table of items
  - Per-row "Retry lookup", per-row input + "Save TMDb link"
  - Status messages ("Fixed: ..." when a manual link resolves the issue)

Place it **after** "Excluded Movies and Series" and **before** "Parental Control" so it sits with the operational controls.

---

## 8. Behavior change summary (per entrypoint)

| Path | Before | After |
|---|---|---|
| Scheduled scan | Serial TMDb requests | Bounded-parallel, polite TMDb client |
| Single-library scan | Serial TMDb requests | Bounded-parallel, polite TMDb client |
| Auto-scan on item-added | One background task per item | Throttled global semaphore (4 default) |
| TMDb client | Single retries on timeout (none on 429) | Exponential backoff, jitter, configurable retries |
| Sequential failures | Crash or slow grind | Optional circuit breaker (configurable threshold) |
| Unmatched item visibility | Debug log line only | Persistent card on the config page |
| Manual TMDb pin for an item | n/a | Persistent map; bypass search |
| Caching | n/a | None (intentional) |

---

## 9. Edge cases & failure handling

1. **`MaxRequestsPerSecond` set to 0** → treat as "use default 4" with a Warning log on startup rather than deadlocking.
2. **`MaxDegreeOfParallelism` collisions with RetryMaxAttempts**: the semaphore is released BEFORE the retry loop, so the retry itself doesn't hold a parallel slot. Retries count against the global rate-limit budget regardless of which worker is doing them.
3. **Manual TMDb-link validation fails (movie with `tv/{id}` form)**: the configured `ManualTmdbLink` can be of the wrong kind; the lookup tries both `/movie/{id}` and `/tv/{id}` and picks the first that returns 200.
4. **`UnmatchedItemsStore` file is corrupt**: on load, log a Warning and start with an empty list. File is corrupt-resilient (`try/catch` around `File.ReadAllText` + JSON parse).
5. **Two scans running concurrently** (full + single-library): each has its own semaphore and circuit-breaker state. Unmatched-items file is updated under a process-wide mutex; last-write-wins is acceptable for this shared resource.
6. **Plugin upgrade**: existing users retain all current settings; new settings default to the conservative defaults above. No migration script needed.
7. **Anime with shared name**: handled by the existing exact-year-first-then-first-result rule in `TmdbService`. If neither matches and the item is unmatched, the user can manually link via the new panel.
8. **`TrackUnmatchedItems=false`**: store is a no-op; no file created. Confirmed not to break existing behavior.
9. **Circuit-breaker aborts mid-scan**: scan summary line states "aborted due to circuit breaker"; `processed`, `modified`, `skipped` written anyway.
10. **HTTP 200 but 401/403**: not retried (auth issues are deterministic); logged Error and item moves to unmatched-items with reason `HttpError`.

---

## 10. Acceptance criteria

A release with this feature is acceptable if:

- [ ] A 10,000-item library scan with default settings completes without crossing 6 req/s against TMDb.
- [ ] On any single 429 response during a scan, the request retries with a configurable delay and does not abort the scan.
- [ ] After scanning, the config page shows an "Unmatched TMDb Items" panel listing every item that returned null from search or release-data.
- [ ] Manually pinning a TMDb ID via the panel makes that exact ID used (bypassing auto-search) on the next scan, and the item disappears from the unmatched list after a successful scan.
- [ ] Existing config (all `1.10.0.0`-era settings) loads without errors on first run after upgrade.
- [ ] No new external dependency (no SQLite, no Polly, no extra NuGet packages). Pure managed code.
- [ ] Token-bucket, retry, circuit-breaker, and unmatched-store all have unit tests passing locally.

---

## 11. Files affected (preliminary)

New files:

- `Services/TokenBucket.cs`
- `Services/ResilientHttpClient.cs`
- `Services/UnmatchedItemsStore.cs`
- `Api/UnmatchedItemsEndpoints.cs` *(optional — endpoints may live directly on the controller)*
- `Configuration/ManualTmdbLink.cs` *(or nested in `PluginConfiguration.cs`)*

Modified files:

- `Configuration/PluginConfiguration.cs` — new fields + nested `ManualTmdbLink` type.
- `Services/TmdbService.cs` — replace direct `HttpClient` calls with `ResilientHttpClient`. Surface null returns via a callback to `UnmatchedItemsStore`.
- `Services/HiddenTagService.cs` — consult `ManualTmdbLinks` before searching; consult `UnmatchedItemsStore` on null.
- `Services/LibraryWatcherService.cs` — throttle fire-and-forget tasks with global semaphore.
- `Tasks/HiddenTagScanTask.cs` — bounded-parallel loop; report circuit-breaker abort cleanly.
- `Api/PhysicalReleaseGuardController.cs` — new endpoints (UnmatchedItems list/clear/retry/link); pass-through resilience knobs from configuration to Task execution.
- `PluginServiceRegistrator.cs` — register `ResilientHttpClient`, `UnmatchedItemsStore`.
- `Configuration/configPage.html` — new sections + new endpoint hooks.
- `manifest.json` and `plugin/meta.json` — bump to 1.11.0.0 with new changelog entry.
- `README.md` — document the new sections and operating modes.

---

## 12. Out-of-scope reminders

- No caching. If upstream contention resurfaces, this spec can be revisited.
- No second metadata source. Anime handling is unchanged; only the new unmatched panel gives an admin a clean way to fix mislinked items by hand.
- No changes to the existing tag semantics, parental-control integration, per-library config, or dry-run mode.
