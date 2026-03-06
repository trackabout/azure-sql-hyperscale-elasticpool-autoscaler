# Checkpoint Before Scaling — Design & Implementation Record

**Date:** 2026-03-05
**Status:** Implemented
**Branch:** `feature/checkpoint-before-scaling`

## Background

When a Hyperscale elastic pool scales up or down, each database undergoes crash recovery. The recovery process replays (redoes) any dirty pages that haven't been flushed to disk, which consumes CPU on the destination tier. The more dirty pages, the longer recovery takes and the longer databases are unavailable to clients.

Running `CHECKPOINT` on each database before scaling flushes dirty pages to disk, minimizing the redo work needed during the transition. The Microsoft Azure SQL Team for Hyperscale recommended this as a best practice to improve scaling speed and reduce client-facing downtime during elastic pool SLO changes.

## Design Decisions

### Checkpoint both scale-up and scale-down

- Scale-down is the critical path (fewer vCores means redo threads are more resource-constrained)
- Scale-up still causes connection drops and crash recovery; checkpoint reduces transition time regardless
- The cost of running CHECKPOINT is negligible compared to the risk of extended database outages

### Best-effort, never blocks scaling

- Individual database checkpoint failures are logged but don't prevent other databases from being checkpointed
- Total checkpoint failure is logged but scaling always proceeds
- Defense-in-depth: `catch (Exception)` in both `AutoScaler` (non-SQL failures) and `SqlRepository` (per-database `SqlException`)

### Bounded concurrency

- `SemaphoreSlim` limits concurrent CHECKPOINT operations (configurable via `CheckpointConcurrency`, default 5, range 1-25)
- Hyperscale pools support max 25 databases per pool
- Prevents overwhelming the pool with parallel checkpoint I/O

### Post-checkpoint delay

- Configurable `PostCheckpointDelaySeconds` (default 3) allows flush settlement before scaling begins
- Set to 0 to skip the delay

## Implementation

### Files modified

| File                          | Change                                                                                                    |
| ----------------------------- | --------------------------------------------------------------------------------------------------------- |
| `ISqlRepository.cs`           | Added `CheckpointDatabasesInPoolAsync` method signature                                                   |
| `SqlRepository.cs`            | Implemented checkpoint with bounded concurrency, parameterized queries, `SqlConnectionStringBuilder`      |
| `AutoScaler.cs`               | Calls checkpoint after dry-run check, before `ScaleElasticPoolAsync`                                      |
| `AutoScalerConfiguration.cs`  | Added `CheckpointConcurrency` and `PostCheckpointDelaySeconds` settings with validation                   |
| `IAutoScalerConfiguration.cs` | Added config interface members                                                                            |
| `AutoScalerTests.cs`          | 7 new tests: call ordering (scale-up/down), error resilience, dry-run skip, hold skip                     |
| `ConfigurationTests.cs`       | 5 new tests: CheckpointConcurrency defaults/custom/validation, PostCheckpointDelaySeconds defaults/custom |
| `Program.cs`                  | Added `AddUserSecrets` for local development                                                              |
| `docs/flowchart.txt`          | Added checkpoint step to Mermaid diagram                                                                  |

### Additional improvements made during this work

- **SQL injection hardening**: Replaced `CreateSqlCompatibleList` string concatenation with Dapper parameterized `IN @PoolNames` across 3 queries
- **Connection string safety**: Replaced `string.Replace("{DatabaseName}")` with `SqlConnectionStringBuilder.InitialCatalog`
- **Async credentials**: Converted `CreateSqlConnection` to `CreateSqlConnectionAsync` using `GetTokenAsync` (avoids blocking thread pool during parallel fan-out)
- **User secrets**: Moved connection strings out of `local.settings.json` into .NET User Secrets for local development
- **Package compatibility**: Reverted `ApplicationInsights.WorkerService` to 2.23.0 (3.0.0 incompatible with Azure Functions v4 runtime)

## Production Timing Data

Checkpoint timing measured across 95 databases in 6 Hyperscale elastic pools (3 prod eastus2, 3 DR centralus):

| Metric       | Value                                  |
| ------------ | -------------------------------------- |
| Min          | 484ms                                  |
| Max          | 2656ms                                 |
| Avg          | 821ms                                  |
| Success rate | 100% (including DR read-only replicas) |

DR replicas are faster (484-576ms typical) vs prod (690-963ms typical). Slowest databases: advancedindustrial, unigas_colombia, virtair, wescoweld.

Script: `scripts/checkpoint-timing.sh`
Results: `checkpoint-timing-2026-03-05-174521.csv`

## Risks and Mitigations

| Risk                                                     | Mitigation                                                                           |
| -------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| Checkpoint takes too long, delaying scaling              | Bounded concurrency; observed max 2.6s across 95 databases                           |
| Checkpoint fails on a database                           | Best-effort: log error, continue with other DBs, proceed to scale                    |
| Database goes offline between enumeration and checkpoint | Polly retry policy handles transient errors; individual failures don't block scaling |
| CheckpointConcurrency misconfigured                      | Validated at startup (1-25); invalid values throw `InvalidOperationException`        |
