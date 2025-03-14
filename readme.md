# Azure SQL Hyperscale Elastic Pool AutoScaler

![License](https://img.shields.io/badge/license-MIT-green.svg)

## Intelligent Scaling with Azure SQL Hyperscale Elastic Pools

Managing database performance and cost at scale is a challenge for any SaaS business. At TrackAbout, Inc. (a Datacor company) we provide a SaaS platform for tracking reusable, returnable containers serving industries like chemicals and packaged gas (oxygen, nitrogen, CO2, acetylene, etc.). With 400+ customers, each with their own SQL database, optimizing infrastructure is critical.

When hyperscale elastic pools were announced, we saw an opportunity. Hyperscale offered the flexibility to scale rapidly, making it a better fit for our largest, most demanding databases—both in cost and performance. But we quickly discovered a key limitation: _Microsoft didn’t provide built-in autoscaling for Hyperscale elastic pools_.

In this repo, we offer our implementation of an automatic scaler, AutoScaler, for Azure SQL DB Hyperscale Elastic Pools.

A single instance of AutoScaler can manage *multiple* elastic pools within a single Azure SQL Server.

To manage multiple Azure SQL Servers, you can run multiple instances of the AutoScaler.

Scaling decisions are made based on looking back at historical elastic pool metrics provided by the `sys.dm_elastic_pool_resource_stats` view within a database inside a Hyperscale pool.

We scale based on monitoring four key metrics:

- Average CPU Percentage
- Average SQL Instance CPU Percentage
- Average Worker Percentage
- Average Data IO Percentage

For our workload, these are the four most important metrics to monitor for scaling operations within an elastic pool. Depending on your workload, you might wish to extend the function to monitor other metrics, such as Log IO Percentage.

Through configuration, you can control:

- High and low threshold settings for each metric
- How far back in time to look at performance metrics (we use a long lookback window and a short lookback window)
- Floor and ceiling vCore levels to keep your pool within desired boundaries

## Scaling Logic

Our solution applies a hysteresis-based approach to prevent rapid, repetitive changes (thrashing) in response to fluctuating metrics. This kind of approach involves delaying a scaling decision by requiring that metrics remain above or below certain thresholds for a sustained period.

We measure averages over two sliding windows, short and long, so that short bursts don’t cause big changes, but extended load does.

The short window (e.g. 5 minutes) is used to detect sudden spikes or surges.

The long window (e.g. 15–30 minutes) is used to confirm sustained load.

For each execution of the function, we calculate:

- shortAvgCPU, longAvgCPU
- shortWorkers, longWorkers
- shortInstCPU, longInstCPU
- shortDataIO, longDataIO

When deciding to scale up:

- If `shortAvgCPU` > `HighCPUPercent` or `shortWorkers` > `HighWorkersPercent` (etc.) AND the `longAvgCPU` also exceeds threshold, that means a real sustained high usage → scale up.
- If only the short window is high but the long window is comfortable, we wait.

When deciding to scale down:

- If both the short and long windows remain below the "Low" thresholds, we reduce capacity.
- If short is below "Low" but long is borderline, we hold.

This double-check on short+long windows helps increase confidence about big changes without ignoring real bursts.

In Summary:

- If **_any_** of the 4 metric averages for **_both_** the long window and the short window are at or above the high threshold of a specific metric → scale up.
- If **_all_** of the 4 metric averages for **_both_** the long window and the short window are at or below the low threshold → scale down.

## Disclaimer

Azure Elastic Pools can incur **VERY HIGH COSTS**.

**YOU** are solely responsible for your own costs should you choose to use this code.

_We accept no responsibility for costs you incur using this code._

We **strongly** recommend you study this project and its unit tests and run your own load tests to ensure this autoscaler behaves in a manner you are comfortable with.

**YOU HAVE BEEN WARNED!**

## Inspiration

This project was inspired by the following Microsoft article:
[Autoscaling Azure SQL Hyperscale](https://techcommunity.microsoft.com/blog/azuresqlblog/autoscaling-azure-sql-hyperscale/1149025)

The original project was intended for use with standalone Hyperscale SQL databases, not Elastic Pools. We needed one for Elastic Pools.

## Identities and Permissions

Azure Managed Identities for SQL connections is supported. You must first enable the system-managed identity for your Azure Function regardless of whether you are using a user-assigned MI. Otherwise, we have found the user-assigned MI will not work. Be sure to set the `AZURE_CLIENT_ID` environment variable equal to the chosen identity's ObjectId.

In order for the managed identity to be able to query the necessary tables in the `master` database, it must be set as an Admin in the Azure SQL Server's settings. You may create an AAD group and place this (and other) identities into this group.

Of course, basic SQL User (username/password style) connection strings also work.

### Required permissions in the `master` database

The ability to query the `master` database of the Azure SQL Server is required. The identity must be able to query:

- `sys.databases`
- `sys.database_service_objectives`
- `sys.dm_operation_status`

### Required permissions within pool databases

The AutoScaler will query performance metrics within individual databases in the pool by reading the view `sys.dm_elastic_pool_resource_stats`. To get the most timely metrics, you must query within a pool database. While the same view is available in `master`, it is a known limitation that metics from `master` are delayed. We've seen them delayed by over 5 minutes.

For our implementation, we decided to pick a pool database _at random_ to query for metrics measurement each time the AutoScaler executes.

We chose this because:

1. Databases come and go. We did not want AutoScaler to break or require reconfiguration if a database was dropped.
2. It seemed unfair to keep hitting the same database every iteration for metrics.
3. We could have chosen to add a new, empty database for querying metrics. However, Hyperscale Elastic Pools support only 25 databases per pool. That's not much, and to sacrifice one simply for metrics querying is wasteful.

Therefore we chose the random method, which should never break and never require reconfiguring.

You might, however, wish to modify the AutoScaler to support a single named database per elastic pool.

### hs.AutoScalerMonitor Table

If you choose to log scaling changes to this table, the managed identity will need INSERT permissions. Leave the connection string blank to disable this feature.

### Elastic Pool Scaling Permissions

The Azure function's identity must have the necessary permission to invoke scaling operations on your elastic pools. To scale an elastic pool, Azure RBAC permissions are needed, specifically the Contributor, SQL DB Contributor role, or SQL Server Contributor Azure RBAC roles. For more information, see [Azure RBAC built-in roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles).

If using a user-assigned managed identity, add a Function environment variable named `AZURE_CLIENT_ID` and set it to the ObjectId of the managed identity you want to use for the function. Since a function app can have more than one managed identity assigned to it, this tells the SDK which identity to use.


## Logging Scaling Actions

Elastic pool scaling actions can be found in Azure Activity Log.

We chose to provide an option to also write scaling operations taken into a `hs.AutoScalerMonitor` table in the SQL database of your choice. This database does not have to live in a Hyperscale Elastic Pool, or any elastic pool.

The connection string is configured in settings.

If you do not wish to log to this table, set this connection string to an empty string.

The script for creating this table is in the `./SQL` folder.

The data logged is useful for understanding how and why the autoscaler acted. It persists the measured metrics at the time of the scaling decision in JSON.

## General Logging

The AutoScaler logs just about everything it does.

Logging is sent to [Azure Application Insights](https://docs.microsoft.com/en-us/azure/azure-functions/functions-monitoring#log-custom-telemetry-in-c-functions), so actions can be monitored from an Azure Portal dashboard or alerted using Azure Monitor.

## Deployment of the Azure Function

Deploy the functino to Azure and set up the function's application settings using the contents of `local.settings.example.json` as a guide.

### Connection Strings

- **MasterSqlConnection**: Connection string to the Azure SQL Server master database.
- **PoolDbConnection**: This is a templatized connection string used to connect to one of the databases in the Elastic Pool. A database in each pool will be chosen at random each time the AutoScaler runs. The AutoScaler must connect to a database within the pool in order to sample the performance metrics. This is because the most rapidly-updated source of the performance metrics is gettable only within a pool database, not the `master` database.
- **MetricsSQLConnection**: Connection string to the Azure SQL Database containing the `hs.AutoScalerMonitor` table. This database can live anywhere you like. The user must have write access to the `hs.AutoScalerMonitor` table. Leave it blank if you don't want to use it.

### Azure Resource Identifiers

- **SubscriptionId**: The Azure subscription ID where the server lives. Needed for scaling operations.
- **SqlInstanceName**: Name of the Azure SQL Server instance where the Elastic Pools are hosted. Needed for scaling operations.
- **ResourceGroupName**: Name of the resource group where the Azure SQL Server is hosted. Needed for scaling operations.
- **ElasticPools**: Comma separated list of Elastic Pools to monitor. You may set a custom VCoreFloor for any given pool by adding a colon and the vCore, like `"PoolName1:8,PoolName2"`. In this case, PoolName1 will be kept at a floor of 8 vCore while PoolName2 will use the global floor.

### Low and High Thresholds

The following low and high thresholds control scaling.

- **LowCpuPercent**, **HighCpuPercent**
- **LowWorkersPercent**, **HighWorkersPercent**
- **LowInstanceCpuPercent**, **HighInstanceCpuPercent**
- **LowDataIoPercent**, **HighDataIoPercent**

### Scaling Management

- **LongWindowLookback**: `900` Size of the long time window looking back at metric averages.
- **ShortWindowLookup**: `300` Size of the short time window looking back at metric averages.
- **MaxExpectedScalingTimeSeconds**: The longest we expect a scaling operation to take. If in-process scaling operations take longer than this, `WARNING` log lines will be written. You can create Azure Monitor Alert Rules to alert on this condition.
- **CoolDownPeriodSeconds**: Microsoft recommends waiting 5-10 minutes (300-600 seconds) before issuing another scaling operation against the same elastic pool. We call this the cool down period.

### Elastic Pool Settings

- **VCoreFloor**, **VCoreCeiling**: The minimum and maximum number of cores to use as bounds for scaling up and down. You may wish to set a hard ceiling to control costs.
- **VCoreOptions**: `"4,6,8..."` The vCore steps you wish to make available for up- and down-scaling.
- **PerDatabaseMaximums**: `"2,4,6..."` Controls the per-database maximum vCore setting for the pool at each step of VCoreOptions. Must map 1:1 with VCoreOptions.


### SQL Connection Resiliency Settings

- **RetryCount**: When attempting to making a SQL connection, the number of retries.
- **RetryInterval**: If a SQL command is interrupted, indicates the number of seconds to use for an exponential back-off delay before retry.

### Sentry

We use https://sentry.io, you may not.

- **IsSentryLoggingEnabled**: Specifies whether the Sentry application monitoring platform is being used for logging errors. Supported values are true and false.
- **SentryDsn**: Specifies the Sentry Dsn. Required if IsSentryLoggingEnabled is set to true.

### Try Run

- **IsDryRun**: If True, AutoScaler will not scale. It will log what it would have done.

## Unit Tests

You may find value in reading our unit tests to understand how the AutoScaler will choose to scale (up, down or hold) given various inputs.

## CPU Load Testing

To load test the autoscaler, first create the `Numbers` test table within each test database in the pool.

You can run the query `./SQL/sample load test command.sql` to create some workload. Tools like [SQL Query Stress](https://github.com/ErikEJ/SqlQueryStress) can be used to execute the query thousands of times across multiple threads in parallel to drive real load.

Every database in a pool has a Per-Database Maximum vCore setting. For example, if have a pool set at 4 vCore and a database with a per-database maximum of 2 vCore, and you slam that database with load, you'll only ever drive the metrics up to ~50%.

You could get away with creating just one database for load testing, but you'll need to update your function settings to keep the per-database max vCore setting equal to the VCoreOptions settings at each step.

Creating two databases isn't terribly hard. We tested with two and three databases. You can run three instances of SQL Query Stress, one to target each database.

Using SQL Query Stress is fairly straightforward.

First, have the AutoScaler running and watch its output. You can build and run it locally or deploy it to Azure.

Launch a copy of SQL Query Stress for each target database. For each:

- Copy the `sample load test command.sql` contents into the text window.
- Click "Database" and authenticate to one of your test databases. Use the Test Connection button.
- Choose a Number of Iterations. I recommend 100,000 or 1,000,000 so it keeps running until you are ready to stop it.
- Choose a Number of Threads. I chose 4.
- Delay between queries (ms): 50

Ignore the Parameter Substitution feature, we aren't using it.

Press `GO` when ready. Follow the output of the AutoScaler to see it in action. You may also query `hs.AutoScalerMonitor` using `select * from hs.AutoScalerMonitor`

Repeat for each test database, making sure to aim each instance of the tool at a different database in the pool.

Hit `CANCEL` in each when you've completed testing.

## Stateless Design

The AutoScaler does store any state from run to run. We can rely solely on the stored metrics within the elastic pool to make decisions on each iteration.

The writing of scaling actions to the `hs.AutoScalerMonitor` is optional, and the AutoScaler does not read from that table.

## Notes and Observations

### Skipping Pools in Cool-Down Period

Microsoft recommends delaying scaling for at least 10 minutes after a pool was last scaled. We have ~42 minutes of operation history available in the sys views. We skip evaluation of pools that were transitioned within the cool-down period.

### Skipping Pools In Transition

While a scaling operation is taking place for a given pool, we check in the `master` database against [`sys.dm_operation_status`](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-operation-status-azure-sql-database?view=azuresqldb-current). If a pool is in any of the transitional states (Pending, In Progress, Cancel in progress), we do nothing else with that pool during this execution. Other pools in the list will still be handled.

### Pool Metrics are Cleared Following a Transition

For about a minute following a scale-up or scale-down operation, the AutoScaler will be unable to read any metrics. The metrics have been cleared, and need to start accumulating again.

A positive implication of this is that when the AutoScaler runs after a transition, it cannot see any of the metrics from before. Thus it cannot be "fooled" into acting based on state prior to the last scaling action.

### Connection Resiliency

We use [Polly](https://www.pollydocs.org/) for resilience when making database connections. After all, it can be tough to connect to a database that's in a transitioning elastic pool.

If you choose to run this AutoScaler, you should be sure your client code abides by [Microsoft's best practices for resiliency when calling Azure SQL databases](https://learn.microsoft.com/en-us/azure/architecture/checklist/resiliency-per-service).

Whenever an elastic pool scales up or down, all clients are disconnected. Not planning for resiliency will result in dropped database connections, aborted transactions and, depending on how your client code is written, data loss.

### Fractional vCore Setting

Hyperscale elastic pools support some fractional values for the per-database max vCore setting. You can set it to 0.25 and 0.5. After 0.5, only whole numbers are available. Our code supports this. We provide the ability to create a custom 1:1 map between vCore steps and your desired per-database max vCore setting.

However, the per-database MINIMUM vCore is always set to 0. If that is not what you want, then you will need to change the code.

### Clamping to Ceiling and Floor

**IMPORTANT:** If a pool's vCore level is set from somewhere else (within the portal, PowerShell, etc.) to a vCore value *outside the bounds of the floor and ceiling settings*, the AutoScaler will snap it back into bounds in short order. This is very important to know in case, perhaps in an emergency, someone attempts to increase a pool beyond the ceiling.

## Possible Enhancements

### Add a "step size" or "gradient reaction"

Currently, we do single-step scaling. This could cause a slower reaction than desirable if CPU is massively over threshold. Or, it could result in small, frequent steps if we cross the threshold by a small margin.

A more refined approach might be to scale multiple steps if we are far beyond specific thresholds, or scale just one step if we're barely over.

For example:

- If (longAvgCPU > HighCPUPercent × 1.3) or (shortAvgCPU > HighCPUPercent × 1.5) — we're REALLY beyond threshold. Jump up by 2 steps.
- If _just_ above threshold, move only 1 step.

Likewise, for downward scaling, if we're 30% below threshold for a significant portion, maybe skip multiple steps downward.

### Consider exponential weighted averages

Not sure if this is needed just yet, but there might be cause to more heavily weight more recent readings over older readings.

### Consider intedependence between metrics

It's often the case that if CPU gets too bogged down, it won't be long before Workers % starts to climb. The same can be said for Avg CPU % and SQL Instance CPU %. These metrics can be inter-related. Perhaps there is some way to detect when one is causing a lagging rise in another, and scale more aggressively.

## Enjoy!

Larry Silverman\
Chief Technology Officer\
TrackAbout, Inc.
