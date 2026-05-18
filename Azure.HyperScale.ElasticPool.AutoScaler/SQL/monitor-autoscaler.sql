select
    *
from
    [hs].[AutoScalerMonitor] as m
cross apply
    openjson(m.UsageInfo) with (
        ElasticPoolCpuLimit int,
        ShortAvgCpu decimal(9,3),
        LongAvgCpu decimal(9,3),
        ShortInstanceCpu decimal(9,3),
        LongInstanceCpu decimal(9,3),
        ShortWorkersPercent decimal(9,3),
        LongWorkersPercent decimal(9,3),
        ShortDataIo decimal(9,3),
        LongDataIo decimal(9,3)
    ) as u
order by m.InsertedAt desc
