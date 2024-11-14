namespace Azure.HyperScale.ElasticPool.AutoScaler;

public class UsageInfo
{
    public string ElasticPoolName { get; set; } = string.Empty;
    public int ElasticPoolCpuLimit { get; set; } = 0;
    public DateTime TimeStamp { get; set; } = DateTime.MinValue;
    public decimal AvgCpuPercent { get; set; } = 0m;
    public decimal WorkersPercent { get; set; } = 0m;
    public decimal AvgInstanceCpuPercent { get; set; } = 0m;
    public int HighCpuCount { get; set; } = 0;
    public DateTime LastHighCpuTime { get; set; } = DateTime.MinValue;
    public int LowCpuCount { get; set; } = 0;
    public DateTime LastLowCpuTime { get; set; } = DateTime.MinValue;

    public int HighWorkerCount { get; set; } = 0;
    public DateTime LastHighWorkerTime { get; set; } = DateTime.MinValue;
    public int LowWorkerCount { get; set; } = 0;
    public DateTime LastLowWorkerTime { get; set; } = DateTime.MinValue;
    public int HighInstanceCpuCount { get; set; } = 0;
    public DateTime LastHighInstanceCpuTime { get; set; } = DateTime.MinValue;

    public int LowInstanceCpuCount { get; set; } = 0;
    public DateTime LastLowInstanceCpuTime { get; set; } = DateTime.MinValue;

    public override string ToString()
    {
        return
            $"\nUsageInfo:" +
            $"\n{"TimeStamp:",-12}{TimeStamp:u}" +
            $"\n{"Pool:",-12}{ElasticPoolName}" +
            $"\n{"vCore:",-12}{ElasticPoolCpuLimit}" +
            $"\n{"Avg CPU:",-18}{AvgCpuPercent,8:F2}%   {"High CPU Count:",-26}{HighCpuCount,6}   {"Low CPU Count:",-26}{LowCpuCount,6}" +
            $"\n{"Workers:",-18}{WorkersPercent,8:F2}%   {"High Worker Count:",-26}{HighWorkerCount,6}   {"Low Worker Count:",-26}{LowWorkerCount,6}" +
            $"\n{"Avg Instance CPU:",-18}{AvgInstanceCpuPercent,8:F2}%   {"High Instance CPU Count:",-26}{HighInstanceCpuCount,6}   {"Low Instance CPU Count:",-26}{LowInstanceCpuCount,6}";
    }
}
