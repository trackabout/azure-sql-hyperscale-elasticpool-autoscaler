# checkpoint-repro

Live diagnostic that reproduces the Dapper single-token sproc-detection trap which caused PR #60's CHECKPOINT feature to silently fail in production (Sentry [TA-HYPERSCALE-AUTOSCALER-2M](https://datacor.sentry.io/issues/TA-HYPERSCALE-AUTOSCALER-2M)).

## What it tests

Six variants of the same logical operation against a real Hyperscale database, using the same Dapper + SqlClient versions as the autoscaler:

| Case | Form | Expected |
|---|---|---|
| A | `conn.ExecuteAsync("CHECKPOINT")` | **FAIL** — Dapper auto-detects single-identifier SQL and sends it as RPC `EXEC CHECKPOINT`, which SQL Server rejects with `Could not find stored procedure 'CHECKPOINT'`. |
| B | `conn.ExecuteAsync("CHECKPOINT;")` | PASS — terminator breaks the heuristic. |
| C | `conn.ExecuteAsync("CHECKPOINT", commandType: CommandType.Text)` | PASS — explicit Text bypasses the heuristic. |
| D | `conn.ExecuteAsync("CHECKPOINT;", commandType: CommandType.Text)` | PASS — current production fix (`SqlRepository.cs:384`). |
| E | Raw `SqlCommand("CHECKPOINT")` | PASS — control showing the bug is in Dapper, not SqlClient. |
| F | Raw `SqlCommand("CHECKPOINT;")` | PASS — control. |

## Run

```
az login                       # uses lsilverman@trkabt.com or whichever identity has DB access
cd tools/checkpoint-repro
dotnet run -c Release -- --server <server-fqdn> --database <db-name>
```

Uses `DefaultAzureCredential`, so any of az-cli login, environment vars, or managed identity work.

## When to re-run

- After bumping the Dapper or Microsoft.Data.SqlClient package version, to confirm the heuristic still behaves as expected and case D still passes.
- If Sentry shows `Could not find stored procedure 'CHECKPOINT'` again — first check this hasn't regressed at `SqlRepository.cs:384`, then re-run this to confirm the package versions haven't changed the heuristic.
