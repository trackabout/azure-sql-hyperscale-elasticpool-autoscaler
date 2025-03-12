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
}
