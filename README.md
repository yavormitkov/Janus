# Project Janus: On-Premise Mass Payment Ingestion & Ledger Engine

An on-premise Proof of Concept (PoC) for inbound mass payment ingestion and deterministic debt settlement, built on **.NET 10** and **SQL Server 2022**. Designed to run inside isolated corporate networks so that national identifiers (EGN) and customer financial histories remain strictly inside internal infrastructure.

---

### Architectural Deep Dive
For state machine definitions, mid-stream/post-commit crash recovery mechanics, SQL locking strategies, and production readiness trade-offs, see the [Janus Architecture Guide](docs/architecture.md).

---

## Core Engineering Principles

* **Decoupled Ingestion & Settlement:** File ingestion (Phase 1) is physically and logically separated from balance allocation (Phase 2).
* **Schema-First Relational Design:** State transitions, concurrency locks, and constraints are enforced at the database level.
* **Single-Target Files:** Every record, entity, interface, and enum resides in its own standalone file.
* **Strict UTC Chronology (DST Immunity):** All timestamps (`SYSUTCDATETIME()`) and worker schedules run strictly in UTC to avoid Daylight Saving Time clock-shift issues during 03:00 AM settlement windows.

---

## End-to-End Payment Lifecycle

1. **Phase 1 — Ingestion Edge (`ClearingIngestionWorker`):** Queues inbound files from `\Inbound\` through a `BoundedChannel` (capacity 1,000) for backpressure and atomically moves them to `\working\` upon dequeue. Validates detached RSA SHA-256 signatures over a single `FileStream`, rewinds the stream, parses rows via `ReadOnlySpan<char>` with an inline fail-fast `HashSet` uniqueness gate, and streams records via a reusable `SqlDataRecord` TVP into `staging.InboundTransactions` (`Processed = 0`) alongside `staging.ProcessedFiles` inside one atomic transaction.
2. **Phase 2 — Set-Based Ledger Engine (`janus_rawdata_processor`):** A `PeriodicTimer` worker executes `ledger.sp_ProcessInboundClearing` inside an explicit transaction (`SET XACT_ABORT ON`). Unmapped EGNs (Status `10`), settled replays (Status `60`), and active suspense duplicates (Status `80`) route to `ledger.SuspenseTransactions` (`Processed = 3`). Valid payments are ordered deterministically by `RowIndex` and processed via a set-based TVP pipeline (`ledger.sp_AllocatePaymentBatch`) using `SUM() OVER` window functions and an Inline Table-Valued Function (`ledger.fn_CalculateWaterfall` via `CROSS APPLY`) to drain balances (`Interest -> Expenses -> Principal`) under `UPDLOCK, ROWLOCK, HOLDLOCK` isolation (`Processed = 1`). Fully settled debts (`TotalDebt = 0`) are flagged with `IsArchived = 1`; subsequent payments for an archived zero-balance debt route 100% straight to `ledger.Overpayments` and emit an Outbox event.
3. **Phase 3 — Back-Office Portal (`JanusLedgerApi` & Blazor):** Local-first Blazor UI behind a YARP reverse proxy using local Bootstrap 5.3.3 assets. Supports Four-Eyes identity corrections (`MakerUserId <> CheckerUserId`, `Processed = 2`) and duplicate triage via direct `LEFT JOIN` to staging (`Processed = 4`).
4. **Phase 4 — Transactional Outbox (`janus_message_processor`):** Overpayments insert into `ledger.Overpayments` alongside a `FOR JSON PATH` event in `ledger.OutboxEvents` within the same transaction. A detached worker drains the queue using `UPDLOCK, ROWLOCK, READPAST` hints, propagating W3C `traceparent` context (`CHAR(55) NOT NULL`) and isolating malformed JSON payloads into a dead-letter state (`IsProcessed = -1`).

---

## Pipeline & Dataflow

<img width="2259" height="8192" alt="janus-diagram" src="https://github.com/user-attachments/assets/545c25ba-248d-43ff-9f89-70fd43cadf22" />


---

## Empirical Benchmarks (50,000-Record Developer Smoke Test)

These measurements represent a local developer baseline (not a controlled server benchmark) executed against a clean SQL Server 2022 Developer Edition container in Docker Desktop (WSL2) on a consumer laptop:

* **Test Environment:** Lenovo IdeaPad (Intel Core i5-10210U @ 1.60GHz, 20 GB DDR4 mixed RAM [4 GB soldered + 16 GB SO-DIMM, single-channel], local NVMe SSD, default WSL2 Docker limits, SQL Server `FULL` recovery model). Observed ~15–20% run-to-run variance due to mobile CPU thermal throttling and WSL2 virtualized disk I/O overhead.
* **Phase 1 — Ingestion Edge:** Stream-parsed and committed **50,000 rows** in a single TVP batch in **~1.3 s (~39,100 rows/sec)**, dominated by database network/write time (in-memory `ReadOnlySpan<char>` parsing accounts for  CheckerUserId`).
* **`SuspenseConcurrencyTests`:** Uses synchronized start gates after connection open to verify that `XLOCK` hints serialize parallel approval attempts on identical suspense rows.
* **`OutboxPoisonMessageTests`:** Confirms malformed JSON payloads isolate into `IsProcessed = -1` without stalling the outbox queue.

---

## Security & Audit Controls

* **SQL Server 2022 Ledger vs. Temporal Tables:** System-versioned temporal tables (`SYSTEM_VERSIONING = ON`) maintain point-in-time row history (`audit.MakerCheckerHistory`), while updatable **SQL Server 2022 Ledger** tables (`ledger.SuspenseTransactions` and its linked `ledger.SuspenseTransactionsHistory` hash chain) provide cryptographic tamper-evidence against unauthorized modifications even by privileged `sysadmin` accounts.
* **Mid-Stream & Post-Commit Recovery:** Uncommitted mid-stream failures roll back cleanly and re-run from `\working\` on restart; post-commit crashes before `\ok\` move are caught by the `staging.ProcessedFiles(FileName)` unique constraint (`SqlException` 2627/2601) without duplicating rows.

---

## Key Production Readiness Trade-Offs

* **Critical Security & Reliability Gaps:** `EGN` values are stored in plaintext without Bulgarian 10-digit checksum validation to support synthetic test data; production requires **Always Encrypted** (deterministic), **TDE**, and full checksum enforcement. Production deployments also require **Polly** retry policies (handling SQL Error 1205 deadlocks) and a periodic fallback directory sweep alongside `FileSystemWatcher` for SMB/NFS shares.
* **Operational & Scaling Considerations:** Production environments require **DbUp/Flyway** schema migrations, **SQL Server Query Store**, verified **PITR** restore drills, and real JWKS validation in YARP. Regional providers (e.g., EasyPay) typically send **under 1,000 transactions per daily file** (with large enterprises peaking at 6,500–15,000 rows/day); linearly extrapolated from the 50k baseline, a 1,000-row batch ingests in **~25 ms** and settles in **under 0.35 seconds** on a single node (excluding fixed connection/transaction startup overhead), making distributed brokers and sharding unnecessary overhead.
