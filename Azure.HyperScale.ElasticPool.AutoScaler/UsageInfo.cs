namespace Azure.HyperScale.ElasticPool.AutoScaler;
public class UsageInfo
{
    public string ElasticPoolName { get; set; } = string.Empty;
    public int ElasticPoolCpuLimit { get; set; }
    public decimal ShortAvgCpu { get; set; }
    public decimal LongAvgCpu { get; set; }
    public decimal ShortInstanceCpu { get; set; }
    public decimal LongInstanceCpu { get; set; }
    public decimal ShortWorkersPercent { get; set; }
    public decimal LongWorkersPercent { get; set; }
    public decimal ShortDataIo { get; set; }
    public decimal LongDataIo { get; set; }

    public override string ToString()
    {
        return
            $"\nUsageInfo:" +
            $"\n{"Pool:",-12}{ElasticPoolName}" +
            $"\n{"vCore:",-12}{ElasticPoolCpuLimit}" +
            $"\n{"Short Avg CPU:",-24}{ShortAvgCpu,8:F2}%   {"Long Avg CPU:",-24}{LongAvgCpu,8:F2}%" +
            $"\n{"Short Instance CPU:",-24}{ShortInstanceCpu,8:F2}%   {"Long Instance CPU:",-24}{LongInstanceCpu,8:F2}%" +
            $"\n{"Short Workers Percent:",-24}{ShortWorkersPercent,8:F2}%   {"Long Workers Percent:",-24}{LongWorkersPercent,8:F2}%" +
            $"\n{"Short Data IO:",-24}{ShortDataIo,8:F2}%   {"Long Data IO:",-24}{LongDataIo,8:F2}%";
    }
}
