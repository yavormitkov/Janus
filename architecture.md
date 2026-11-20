# Project Janus: On-Premise Mass Payment Ingestion & Ledger Architecture

An on-premise Proof of Concept (PoC) for inbound mass payment ingestion and deterministic debt settlement, built on .NET 10 and SQL Server 2022. Designed to run inside isolated corporate networks so that national identifiers (EGN) and customer financial histories remain strictly inside internal infrastructure.

The architecture focuses on solving specific operational problems in batch payment processing: partial file failures, thread-pool exhaustion under I/O bursts, concurrent balance updates, and unauthorized manual adjustments.

---

## Core Engineering Constraints

* **Decoupled Ingestion & Settlement:** File ingestion (Phase 1) is physically and logically separated from balance allocation (Phase 2).
* **Schema-First Relational Design:** State transitions, locking strategies, and constraints are defined at the schema level before application coding.
* **Single-Target Files:** Every entity, record, interface, and enum resides in its own file to keep codebase navigation predictable.
* **Structured Namespace Zoning:** Directories follow strict pluralized conventions (`\Commands\`, `\Queries\`, `\DTOs\`, `\BackgroundServices\`).
* **Strict UTC Chronology (DST Immunity):** All database timestamps (`SYSUTCDATETIME()`), audit logs, and worker schedules operate strictly in UTC to eliminate Daylight Saving Time (DST) clock-shift anomalies during 03:00 AM settlement windows.

---

## End-to-End Payment Lifecycle

1. **Phase 1 — Cryptographic Verification & Bulk Staging (`ClearingIngestionWorker`):**
   External provider files (flat-file or XML) land in `\Inbound\` and are queued into a `BoundedChannel` (capacity 1,000) for backpressure before being atomically moved to `\working\` upon worker dequeue. A single `FileStream` is passed through detached RSA SHA-256 signature verification (`IFileSignatureVerifier`) without loading the file into a byte array. After rewinding the stream, polymorphic parsers (`IClearingParser`) slice fields using `ReadOnlySpan<char>`, enforce an inline `HashSet` uniqueness gate (failing fast on intra-file duplicates), and reconcile mathematical trailer totals. Valid batches are streamed into `staging.InboundTransactions` (`Processed` state `0: Pending`) and logged in `staging.ProcessedFiles` (`MIN(TraceId)`) inside a single atomic transaction via a reusable `SqlDataRecord` TVP iterator. Valid files move to `\ok\`; invalid payloads move to `\error\` alongside a `.error` diagnostic log.

2. **Phase 2 — Set-Based Waterfall Settlement (`janus_rawdata_processor`):**
   A `PeriodicTimer` worker polls unprocessed staging batches and executes `ledger.sp_ProcessInboundClearing` inside an explicit transaction (`SET XACT_ABORT ON`):
   * **Anomaly Quarantine:** Unmapped EGNs route to `ledger.SuspenseTransactions` as Status `10` (Unresolved). Transactions matching `ledger.IdempotencyRegistry` route as Status `60` (Duplicate), while active unmitigated duplicates in suspense route as Status `80` (SuspenseDuplicate). Staging rows are marked as `Processed = 3` (Suspense).
   * **Set-Based Balance Allocation:** Matched payments are ordered deterministically by `RowIndex`, loaded into a strongly-typed TVP (`ledger.PaymentAllocationBatchType`), and processed via `ledger.sp_AllocatePaymentBatch` without row-by-row loops. Using cumulative window functions (`SUM() OVER`) to handle multiple payments for the same client within one batch and an Inline Table-Valued Function (`ledger.fn_CalculateWaterfall` via `CROSS APPLY`), the engine drains balances under row-level locks (`UPDLOCK, ROWLOCK, HOLDLOCK`) in strict legal order (`Interest -> Expenses -> Principal`) via a single bulk `UPDATE`. Fully settled debts (`TotalDebt = 0`) are flagged with `IsArchived = 1` (if a subsequent payment arrives for an archived zero-balance debt, the waterfall routes 100% of the amount straight to `ledger.Overpayments` and emits an Outbox event without un-archiving the settled debt), and staging rows are marked as `Processed = 1` (AutoProcessed).

3. **Phase 3 — Back-Office Mitigation (`JanusLedgerApi` & Blazor Portal):**
   Operators resolve quarantined records without re-ingesting files:
   * **Maker-Checker Identity Fixes:** A Maker proposes an EGN correction (Status `20: PendingApproval`), and a distinct Checker approves it (Status `30: Cleared`), triggering payment allocation, updating staging to `Processed = 2` (ManuallyProcessed), and recording the action in `audit.MakerCheckerHistory`.
   * **Duplicate Triage:** Operators inspect duplicate entries (`60` & `80`) paired against their original staging records via a direct `LEFT JOIN`, closing them via `ledger.sp_CloseSuspenseTransaction` as either soft-deleted rejections (Status `40: Rejected`) or outbound provider refunds (Status `70: ReturnToProvider`), marking staging as `Processed = 4` (Error).

4. **Phase 4 — Transactional Outbox (`janus_message_processor`):**
   When payments exceed total debt, the surplus is written to `ledger.Overpayments` alongside an integration event generated via `FOR JSON PATH` in `ledger.OutboxEvents` within the same transaction. A detached polling worker drains this table using `UPDLOCK, ROWLOCK, READPAST` hints to allow non-blocking parallel reads, propagating the W3C `traceparent` context and isolating malformed JSON payloads into a dead-letter state (`IsProcessed = -1`). In this PoC, processed events emit OpenTelemetry spans and logs as an integration hook for downstream ERP or rebate workflows.

---

## Pipeline & Dataflow

<img width="2259" height="8192" alt="janus-diagram" src="https://github.com/user-attachments/assets/2c03ff92-9e38-4553-aff8-123be0449795" />


## Resilience, Observability & Benchmarks

### 1. Mid-File & Post-Commit Crash Recovery (`\working\` State)
* **Mid-Stream Crash (Before DB Commit):** Phase 1 wraps the `staging.InboundTransactions` TVP insert and the `staging.ProcessedFiles` log entry inside a single database transaction. If the worker crashes mid-stream, SQL Server rolls back the uncommitted transaction, leaving zero partial rows. Upon restart, `GetCrashRecoveredWorkingFiles()` picks up the untouched file from `\working\` and processes it from byte zero.
* **Post-Commit Crash (Before `\ok\` Move):** If the worker crashes *after* the database transaction commits but *before* `MoveToOk()` executes on disk, the file remains in `\working\`. Upon restart, the unique constraint on `staging.ProcessedFiles(FileName)` rejects the duplicate staging insert (`SqlException` 2627/2601), routing the file to `\error\` with a diagnostic trace without duplicating database rows.

### 2. Multi-Tier Duplicate & Replay Defense
* **Intra-File Gate (Phase 1 Parser):** An inline `HashSet` validates `ReferenceNumber` uniqueness during stream parsing, aborting the file immediately upon encountering a duplicate line.
* **File-Level Gate (Phase 1 Repository):** Processed file names are committed to `staging.ProcessedFiles` with a unique constraint, blocking accidental re-ingestion of identical batch files.
* **Transaction-Level Quarantine (Phase 2 Ledger):** Settled transactions matched against `ledger.IdempotencyRegistry` (enforcing global uniqueness per `ReferenceNumber` regardless of debt archival status) are diverted to `ledger.SuspenseTransactions` as Status `60`. Unsettled references matching active suspense items are diverted as Status `80`.

### 3. OpenTelemetry & Grafana Integration
* **W3C `traceparent` Propagation (`CHAR(55) NOT NULL`):** Database tables persist the fixed-length 55-character W3C `traceparent` string across state transitions. Phase 1 captures `Activity.Current`, while downstream workers and APIs rebuild parent-child span graphs via `ActivityContext.TryParse`.
* **OTLP Metrics:** Ingestion batch counts, execution durations, memory deltas, and fault tags export via OpenTelemetry Protocol (OTLP) collectors to an on-premise Grafana/Prometheus stack.
* **Poison Message Isolation:** `janus_message_processor` wraps JSON deserialization in a per-row sandbox. A `JsonException` records an `ActivityEvent` and marks the row as `IsProcessed = -1` (Dead-Letter), preventing queue starvation.

### 4. Empirical Benchmarks (50,000-Record Developer Smoke Test)

These measurements represent a local developer baseline (not a controlled server benchmark) executed against a clean SQL Server 2022 Developer Edition container running inside Docker Desktop (WSL2):

* **Test Environment:** Lenovo IdeaPad (Intel Core i5-10210U @ 1.60GHz, 4 Cores / 8 Threads, 20 GB DDR4 mixed RAM [4 GB soldered + 16 GB SO-DIMM, single-channel], local NVMe SSD, default WSL2 container limits, SQL Server `FULL` recovery model). Observed ~15–20% run-to-run variance due to mobile CPU thermal throttling and WSL2 virtualized disk I/O penalties.
* **Phase 1 — Ingestion Edge:** Stream-parsed, validated, and committed **50,000 rows** in a single TVP batch in **~1.3 s (~39,100 rows/sec)**, dominated by database write latency (`ReadOnlySpan<char>` parsing takes  CheckerUserId)` (`CK_SuspenseTransactions_MakerChecker_Distinct`), with approved changes logged in `audit.MakerCheckerHistory`.
* **Concurrency Control:** Back-office suspense updates acquire `ROWLOCK, XLOCK` hints within the transaction to reject concurrent approvals on the same record.
* **SQL Server 2022 Ledger vs. Temporal Tables:** System-versioned temporal tables (`SYSTEM_VERSIONING = ON`) maintain point-in-time row history (`audit.MakerCheckerHistory`), while updatable **SQL Server 2022 Ledger** tables (`ledger.SuspenseTransactions` and its linked `ledger.SuspenseTransactionsHistory` + hash chain) provide cryptographic tamper-evidence against unauthorized modifications even by privileged `sysadmin` accounts.
* **Local-First Back-Office UI:** The Blazor operator portal runs behind a YARP reverse proxy using local static assets (Bootstrap 5.3.3) with zero external CDN dependencies.

---

## Database Schema Segregation (`JanusFinancialDb`)

* **`staging` schema:** Append-only landing tables (`InboundTransactions`, `ProcessedFiles`) storing raw batch rows, W3C `traceparent` strings, and integer processing states (`Processed`: `0` Pending, `1` AutoProcessed, `2` ManuallyProcessed, `3` Suspense, `4` Error).
* **`ledger` schema:** Transactional core (`Clients`, `Debts`, `SuspenseTransactions`, `IdempotencyRegistry`, `Overpayments`, `OutboxEvents`) housing set-based stored procedures and inline TVFs.
* **`audit` schema:** System-versioned history tables (`MakerCheckerHistory`) enforcing server-side Maker-Checker constraints.

---

## Real-World Trade-Offs & Production Readiness Gaps

Because this repository is a local PoC focused on core ingestion mechanics and relational concurrency, several production requirements are intentionally simplified and must be addressed before live deployment:

### Critical Security & Reliability Gaps

1. **Encryption at Rest for PII & EGN Checksum Validation:** In this PoC, `EGN` values are stored in plaintext and validated structurally without enforcing the Bulgarian 10-digit control-digit checksum algorithm (to allow synthetic test IDs). In production, parsers must enforce the mathematical EGN checksum, while `EGN` columns must use **SQL Server Always Encrypted** (deterministic encryption for exact `JOIN` and `WHERE` lookups) combined with **Transparent Data Encryption (TDE)**.
2. **Connection Resiliency & Transient Fault Handling:** Background workers rely on standard `SqlClient` connection pooling without custom exponential backoff policies. Production deployments should wrap transient database connection openings and deadlocks (SQL Error 1205) in **Polly** retry pipelines with jitter.
3. **FileSystemWatcher Reliability on Network Shares:** Standard OS `FileSystemWatcher` events can drop under high burst loads (internal 64KB buffer overflow) or over SMB/NFS network mounts. Production deployments must pair `FileSystemWatcher` with a periodic fallback directory sweep on `\Inbound\` to guarantee zero missed files.

### Operational & Scaling Considerations

4. **Database Migrations, Observability & Verified Disaster Recovery:** Schema deployment currently relies on raw SQL scripts. Production environments require versioned migration pipelines (**DbUp** or **Flyway**), **SQL Server Query Store** enabled for execution plan regression tracking, and automated **Point-in-Time Recovery (PITR)** log backups paired with scheduled automated restore drills to verify backup integrity.
5. **Domain Volumetrics vs. Distributed Sharding:** Production experience with regional payment providers (e.g., EasyPay) shows that standard daily clearing batches typically contain **under 1,000 transactions per file** (with large enterprises peaking at 6,500–15,000 daily rows). Linearly extrapolated from the 50k baseline, a 1,000-row batch ingests in **~25 ms** and settles in **under 0.35 seconds** on a single node (excluding fixed connection/transaction startup overhead). Multi-node sharding and external brokers (Kafka/RabbitMQ) are intentionally omitted to avoid distributed transaction overhead and split-brain failure modes; long-term data growth (100M+ rows) is handled via native SQL Server table partitioning on `CreatedAt`.
6. **Gateway Identity Simulation:** The YARP reverse proxy injects simulated `X-User-Id` and `X-User-Role` headers for demonstration purposes. Production deployment requires wiring YARP to an enterprise Identity Provider (Microsoft Entra ID / Keycloak) with asymmetric JWKS signature validation.
