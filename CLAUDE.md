# Azure SQL Hyperscale Elastic Pool AutoScaler

## Project Overview

Open-source autoscaler for Azure SQL Hyperscale Elastic Pools, filling a gap where Microsoft provides no built-in autoscaling. Built by Larry Silverman / TrackAbout, Inc. Blog post: https://devblogs.microsoft.com/azure-sql/auto-scaling-hyperscale-elastic-pools/

## Tech Stack

- **Language**: C# / .NET 8
- **Runtime**: Azure Functions v4 (isolated worker model)
- **Testing**: xUnit + Moq
- **SQL**: Dapper + Microsoft.Data.SqlClient
- **Azure SDK**: Azure.ResourceManager.Sql for scaling operations
- **Resilience**: Polly for SQL connection retries
- **Monitoring**: Application Insights + optional Sentry
- **Auth**: Azure Managed Identity (user-assigned via `AZURE_CLIENT_ID`) or SQL username/password

## Architecture

Timer-triggered Azure Function (`*/15 * * * * *` = every 15 seconds) that:
1. Checks Azure RBAC permissions
2. Queries `sys.dm_elastic_pool_resource_stats` from a **random** pool database (not master, for freshest metrics)
3. Evaluates 4 metrics across short + long sliding windows (hysteresis to prevent thrashing)
4. Scales via Azure Resource Manager API if thresholds crossed

### Scaling Logic

- **Scale Up**: ANY metric exceeds high threshold in BOTH short and long windows
- **Scale Down**: ALL metrics below low thresholds in BOTH windows
- **Hold**: Everything else
- Scale-up can skip steps (`ScaleUpSteps` setting); scale-down always moves one step
- Pools in transition or cool-down period are skipped

### Key Classes

| File | Purpose |
|------|---------|
| `AutoScaler.cs` | Core scaling logic — `DoTheThing()` entry point, `GetScalingAction()`, `GetServiceLevelObjective()` |
| `AutoScalerConfiguration.cs` | All config from `IConfiguration`, validation in constructor |
| `IAutoScalerConfiguration.cs` | Config interface for testability |
| `SqlRepository.cs` | Queries `sys.dm_elastic_pool_resource_stats`, `sys.databases`, `sys.dm_operation_status` |
| `AzureResourceService.cs` | Azure SDK calls to scale elastic pools, check permissions |
| `ErrorRecorder.cs` | Error logging abstraction |
| `UsageInfo.cs` | DTO for short/long window metric averages per pool |
| `PoolTargetSettings.cs` | DTO for target vCore + per-database max capacity |
| `Program.cs` | DI setup, host configuration |

## Development Workflow

Use TDD red/green: write failing tests first, then implement the minimum code to make them pass. Do not write implementation before the corresponding test exists and fails.

When editing C# files, use Serena's symbolic tools or LSP for type-aware editing rather than blind string replacement.

```bash
# Build
dotnet build

# Run tests
dotnet test

# Run locally (requires local.settings.json with valid connection strings)
cd Azure.HyperScale.ElasticPool.AutoScaler && func start
```

## Test Structure

Tests are in `Azure.HyperScale.ElasticPool.AutoScaler.Tests/`:
- `AutoScalerTests.cs` — scaling decisions (up/down/hold), floor/ceiling clamping, dry run
- `ConfigurationTests.cs` — config validation (invalid thresholds, missing values)
- `ScaleUpStepsTests.cs` — multi-step scale-up behavior

Tests mock `IAutoScalerConfiguration`, `ISqlRepository`, `IAzureResourceService`, and `IErrorRecorder`.

## Configuration

All settings come from Azure Function app settings / `local.settings.json`. Key settings:

- **Connection strings**: `MasterSqlConnection`, `PoolDbConnection` (templatized), `MetricsSqlConnection` (optional, for hs.AutoScalerMonitor table)
- **Thresholds**: `LowCpuPercent`/`HighCpuPercent`, `LowWorkersPercent`/`HighWorkersPercent`, `LowInstanceCpuPercent`/`HighInstanceCpuPercent`, `LowDataIoPercent`/`HighDataIoPercent`
- **Windows**: `LongWindowLookback` (default 900s), `ShortWindowLookback` (default 300s)
- **Bounds**: `VCoreFloor`, `VCoreCeiling`, `VCoreOptions` (comma-separated), `PerDatabaseMaximums` (1:1 with VCoreOptions)
- **Behavior**: `ScaleUpSteps` (default 1), `CoolDownPeriodSeconds`, `MaxExpectedScalingTimeSeconds`, `IsDryRun`, `CheckpointConcurrency` (default 5, range 1-25, max concurrent CHECKPOINT operations per pool before scaling), `PostCheckpointDelaySeconds` (default 3, seconds to wait after checkpoint completes before scaling)
- **ElasticPools**: Comma-separated, optional per-pool vCore floor via `PoolName:vCore` syntax

## Important Behaviors

- Elastic pool scaling **drops all client connections** — clients must implement retry logic
- Pools are clamped to floor/ceiling — if set externally outside bounds, AutoScaler snaps back
- Metrics clear after scaling; ~1 minute of no data prevents acting on stale pre-scale metrics
- Random database selection for metrics avoids single-point dependency and config churn
- Hyperscale pools support max 25 databases per pool
