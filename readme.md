# Azure SQL Hyperscale Elastic Pool Autoscaler

![License](https://img.shields.io/badge/license-MIT-green.svg)

## About

This is an implementation of an automatic scaling algorithm for Azure SQL DB Hyperscale Elastic Pools within an Azure Function.

A single instance of this function can manage *multiple* elastic pools within a single Azure SQL Server.

To manage multiple Azure SQL Servers, run multiple instances of the function.

Scaling decisions are made based on looking back at historical elastic pool metrics provided by the `sys.dm_elastic_pool_resource_stats` view. We look at three key metrics:

- Average CPU Percentage
- Average Instance CPU Percentage
- Worker Percentage

In our experience at TrackAbout operating Azure SQL elastic pools since 2016, these are the three most important CPU-related metrics to monitor for scaling operations.

Through configuration, you can control:

- High and low threshold settings for each metric
- How far into the past to look at metrics
- How many low or high thresholds we must exceed to scale
- Floor and ceiling vCore levels to keep your pool within desired boundaries.

We use a hysteresis approach to prevent rapid, repetitive changes (thrashing) in response to fluctuating metrics. This involves delaying the scaling decision or requiring that metrics remain above or below certain thresholds for a sustained period before triggering a scaling action.

## Disclaimer

Azure Elastic Pools can incur **VERY HIGH COSTS**.

**YOU** are solely responsible for your own costs should you choose to use this project.

*We accept no responsibility for costs you incur using this code.*

**YOU HAVE BEEN WARNED!**

We **strongly** recommend you run load tests to ensure this autoscaler behaves in a manner you are comfortable with.

## Inspiration

This project was inspired by the following Microsoft article:
[Autoscaling Azure SQL Hyperscale](https://techcommunity.microsoft.com/blog/azuresqlblog/autoscaling-azure-sql-hyperscale/1149025)

The original project was intended for use with standalone Hyperscale SQL databases, not Elastic Pools. We needed one for Elastic Pools.

## Identities and Permissions

Database connections are made using Managed Identity trust. You must create either a system-managed identity or a user-assigned managed identity for your Azure Function, and use that identity to grant the necessary permissions within your Azure SQL Server and database environment.

### Permissions in the `master` database

The ability to query the `master` database of the Azure SQL Server is required. The identity must be able to query:

- `sys.databases`
- `sys.database_service_objectives`
- `sys.dm_operation_status`

### Permissions within pool databases

The function will query performance metrics within individual databases in the pool by reading the view `sys.dm_elastic_pool_resource_stats`. To get the most timely metrics, you must query into a pool database. While metrics are available in `master`, it is a known limitation that those metrics can be significantly delayed. We've seen them delayed by over 5 minutes.

For our implementation, we decided to pick a pool database *at random* to query for metrics measurement each time the function executes.

We chose this because:

1. Databases come and go. We did not want our autoscaler to break or to have to reconfigure the function if our chosen target database was dropped.
2. It seemed unfair to keep hitting the same database every iteration for metrics.
3. We could have chosen to add a new, empty database for querying metrics. However, Hyperscale Elastic Pools support only 25 databases per pool. That's not much, and to sacrifice one simply for metrics querying felt wasteful.

Therefore we chose the random method, which should never break and never require reconfiguring.

You might, however, wish to modify the function to support a single named database per elastic pool.

### hs.AutoScalerMonitor Table

If you choose to log to this table, the managed identity will need INSERT permissions. If you leave the connection string blank, no attempt will be made.

### Elastic Pool Scaling Permissions

The managed identity must have the necessary permission to invoke scaling operations on your elastic pools. To scale an elastic pool, Azure RBAC permissions are needed, specifically the Contributor, SQL DB Contributor role, or SQL Server Contributor Azure RBAC roles. For more information, see [Azure RBAC built-in roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles).

## Deployment Guidance

### Azure SQL

We store historical metrics in a `hs.AutoScalerMonitor` table in the SQL database of your choice. This database does not have to live in a Hyperscale Elastic Pool, or any elastic pool.

The connection string is configured in settings.

If you do not wish to log to this table, set this connection string to an empty string.

The script for creating this table is in the `./SQL` folder.

The data logged is useful for understanding how and why the autoscaler acted.

### Logging

The function logs just about everything it does.

Logging is sent to [Azure Application Insights](https://docs.microsoft.com/en-us/azure/azure-functions/functions-monitoring#log-custom-telemetry-in-c-functions), so actions can be monitored from an Azure Portal dashboard or alerted using Azure Monitor.

### Azure Function

Deploy the solution to an Azure Function and then set up the application settings using the contents of `local.settings.example.json` as a template.

#### Connection Strings

- **MasterSqlConnection**: Connection string to the Azure SQL Server master database.
- **PoolDbConnection**: This is a templatized connection string used to connect to one of the databases in the Elastic Pool. A database in each pool will be chosen at random each time the function runs. The function must connect to a database within the pool in order to sample the performance metrics. This is because the most rapidly-updated source of the performance metrics is gettable only within a pool database, not the `master` database.
- **MetricsSQLConnection**: Connection string to the Azure SQL Database containing the `hs.AutoScalerMonitor` table. This database can live anywhere you like. The user must have write access to the `hs.AutoScalerMonitor` table. Leave it blank if you don't want to use it.

#### Settings

- **SubscriptionId**: The Azure subscription ID where the server lives. Needed for scaling operations.
- **SqlInstanceName**: Name of the Azure SQL Server instance where the Elastic Pools are hosted. Needed for scaling operations.
- **ResourceGroupName**: Name of the resource group where the Azure SQL Server is hosted. Needed for scaling operations.
- **ElasticPools**: Comma separated list of Elastic Pools to monitor.
- **LowCpuPercent**, **HighCpuPercent**: Average CPU Percent low and high thresholds.
- **LowWorkersPercent**, **HighWorkersPercent**: Workers Percent low and high thresholds.
- **LowInstanceCpuPercent**, **HighInstanceCpuPercent**: SQL Instance CPU Percent low and high thresholds.
- **LookBackSeconds**, **LowCountThreshold**, **HighCountThreshold**: Hysteresis-controlling settings. More on these below.
- **VCoreFloor**, **VCoreCeiling**: The minimum and maximum number of cores to use as bounds for scaling up and down. You'll probably always set VCoreFloor to the minimum vCore setting possible. You may wish to set a hard ceiling to control costs.
- **VCoreOptions**: The list of available vCore options for the type of Hyperscale Elastic Pool being used. Copied from Azure documentation.
- **PerDatabaseMaximums**: Controls the per-database maximum vCore setting for the pool at each step. Should map 1:1 with VCoreOptions.

## Hysteresis Configuration

The Hyperscale elastic pool environment generally posts new performance metrics every 15 seconds or so. That is why this function is initially set to run every 15 seconds. You will want to set parameters that provide a balance between responsiveness and stability. Here are some recommendations based on the metrics frequency:

### 1. **LookBackSeconds**

**Recommended Range**: 900–1800 seconds (15–30 minutes). Since metrics are generated every 15 seconds, this window will yield 60–120 data points in 15–30 minutes.

**Rationale**: A 15- to 30-minute lookback window is usually sufficient to capture significant trends in usage without being overly reactive to short spikes or dips.

Shorter windows (e.g., 5–10 minutes) would likely capture transient behavior rather than a consistent trend, potentially leading to frequent, unnecessary scaling actions.

### 2. **HighCountThreshold**

**Recommended Range**: 5–10

With a 15-minute lookback window (900 seconds), a threshold of 5 would mean you need about 75 seconds (5 readings at 15 seconds each) of high utilization to trigger a scaling up decision.

If you set it at 10, then you require 150 seconds (2.5 minutes) of high utilization.

**Rationale**:
This threshold should be high enough that transient usage spikes don’t cause scaling up but low enough to ensure responsiveness to genuine demand increases.

For critical workloads, you might opt for a lower threshold (closer to 5), while for less urgent workloads, you could set it closer to 10.

### 3. **LowCountThreshold**

**Recommended Range**: 10–15

With a lookback window of 900 seconds, a threshold of 10 would require around 2m30s of consistently low utilization to scale down.

A threshold of 15 requires around 225 seconds (3m45s).

**Rationale**:
This threshold should require more sustained low utilization than the high threshold to avoid rapid downscaling that could impair performance if usage temporarily increases again.

By setting a higher threshold here, you add a degree of stability, helping to ensure that the system is genuinely underutilized before scaling down.

### Summary

With these settings:

- **LookBackSeconds**: 900–1800 (15–30 minutes)
- **HighCountThreshold**: 5–10
- **LowCountThreshold**: 10–15

This approach allows for stable and responsive scaling while preventing frequent fluctuations due to transient metrics. You may want to test these configurations in a staging environment, adjusting them based on actual workload patterns.

## Unit Tests

You may find value in reading our unit tests to understand how the function will choose to scale (up, down or hold) given various inputs.

## Load Testing

To load test the autoscaler, first create the `Numbers` test table within each test database in the pool.

You can run the query `./SQL/sample load test command.sql` to create some workload. Tools like [SQL Query Stress](https://github.com/ErikEJ/SqlQueryStress) can be used to execute the query thousands of times across multiple threads in parallel to drive real load.

Every database in a pool has a Per-Database Maximum vCore setting. For example, if have a pool set at 4 vCore and a database with a per-database maximum of 2 vCore, and you slam that database with load, you'll only ever drive the metrics up to ~50%.

You could get away with creating just one database for load testing, but you'll need to update your function settings to keep the per-database max vCore setting equal to the VCoreOptions settings at each step.

Creating two databases isn't terribly hard. We tested with two and three databases. You can run three instances of SQL Query Stress, one to target each database.

Using SQL Query Stress is fairly straightforward.

First, have the Azure Function running and watch its output. You can build and run it locally or deploy it to Azure.

Launch a copy of SQL Query Stress for each target database. For each:

- Copy the `sample load test command.sql` contents into the text window.
- Click "Database" and authenticate to one of your test databases. Use the Test Connection button.
- Choose a Number of Iterations. I recommend 100,000 or 1,000,000 so it keeps running until you are ready to stop it.
- Choose a Number of Threads. I chose 4.
- Delay between queries (ms): 50

Ignore the Parameter Substitution function, we aren't using it.

Press `GO` when ready. Follow the output of the Azure function to see it in action. You may also query `hs.AutoScalerMonitor` using `select * from hs.AutoScalerMonitor`

Repeat for each test database, making sure to aim each instance of the tool at a different database in the pool.

Hit `CANCEL` in each when you've completed testing.

## Stateless Design

This function does store any state from run to run. We can rely solely on the stored metrics within the elastic pool to make decisions on each iteration.

The writing of scaling actions to the `hs.AutoScalerMonitor` is optional, and the function does not read from that table.

## Notes and Observations

### Skipping Pools In Transition

While a scaling operation is taking place for a given pool, we check in the `master` database against (`sys.dm_operation_status`)[https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-operation-status-azure-sql-database?view=azuresqldb-current]. If a pool is in any of the transitional states (Pending, In Progress, Cancel in progress), we do nothing else with that pool during this execution of the function. Other pools in the list will still be handled.

### Pool Metrics are Cleared Following a Transition

For about a minute following a scale-up or scale-down operation, the function will be unable to read any metrics. The metrics have been cleared, and need to start accumulating again.

A positive implication of this is that when the function runs after a transition, it cannot see any of the metrics from before. Thus it cannot be "fooled" into acting based on the earlier state.

### Connection Resiliency

We use [Polly](https://www.pollydocs.org/) for resilience when making database connections. After all, it can be tough to connect to a database that's in a transitioning elastic pool.

If you choose to run this AutoScaler, you should be sure your client code abides by [Microsoft's best practices for resiliency when calling Azure SQL databases](https://learn.microsoft.com/en-us/azure/architecture/checklist/resiliency-per-service).

Whenever an elastic pool scales up or down, all clients are disconnected. Not planning for resiliency will result in dropped database connections, aborted transactions and, depending on how your client code is written, data loss.

### Fractional vCore Setting

Hyperscale elastic pools support some fractional values for the per-database max vCore setting. You can set it to 0.25 and 0.5. After 0.5, only whole numbers are available. Our code supports this. We provide the ability to create a custom 1:1 map between vCore steps and your desired per-database max vCore setting.

However, the per-database MINIMUM vCore is always set to 0. If that is not what you want, then you will need to change the code.

### Clamping to Ceiling and Floor

**IMPORTANT:** If a pool's vCore level is set from somewhere else (within the portal, PowerShell, etc.) to a vCore value *outside the bounds of the floor and ceiling settings*, the AutoScaler will snap it back into bounds in short order. This is very important to know in case, perhaps in an emergency, someone attempts to increase a pool beyond the ceiling.

