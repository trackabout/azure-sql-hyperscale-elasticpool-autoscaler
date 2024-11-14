select
    *
from 
    [hs].[AutoScalerMonitor] as m
cross apply
    openjson(m.UsageInfo) with (
        TimeStamp datetime,
        ElasticPoolCpuLimit int,
        AvgCpuPercent decimal(9,3),
        MovingAvgCpuPercent decimal(9,3),
        WorkersPercent decimal(9,3),
        MovingAvgWorkersPercent decimal(9,3),
        AvgInstanceCpuPercent decimal(9,3),
        MovingAvgInstanceCpuPercent decimal(9,3),
        DataPoints int
    ) as u
order by m.InsertedAt desc
