namespace Azure.HyperScale.ElasticPool.AutoScaler;
// TODO: Remove all unneeded properties
public class UsageInfo
{
    public string ElasticPoolName { get; set; } = string.Empty;
    public int ElasticPoolCpuLimit { get; set; }
    public DateTime TimeStamp { get; set; } = DateTime.MinValue;

    public decimal AvgCpu { get; set; }
    public decimal ShortAvgCpu { get; set; }
    public decimal LongAvgCpu { get; set; }
    public int HighCpuCount { get; set; }
    public int LowCpuCount { get; set; }

    public decimal AvgInstanceCpu { get; set; }
    public decimal ShortInstanceCpu { get; set; }
    public decimal LongInstanceCpu { get; set; }
    public int HighInstanceCpuCount { get; set; }
    public int LowInstanceCpuCount { get; set; }

    public decimal WorkersPercent { get; set; }
    public decimal ShortWorkersPercent { get; set; }
    public decimal LongWorkersPercent { get; set; }
    public int HighWorkerCount { get; set; }
    public int LowWorkerCount { get; set; }

    public decimal AvgDataIo { get; set; }
    public decimal ShortDataIo { get; set; }
    public decimal LongDataIo { get; set; }
    public int HighDataIoCount { get; set; }
    public int LowDataIoCount { get; set; }


    public override string ToString()
    {
        return
            $"\nUsageInfo:" +
            $"\n{"TimeStamp:",-12}{TimeStamp:u}" +
            $"\n{"Pool:",-12}{ElasticPoolName}" +
            $"\n{"vCore:",-12}{ElasticPoolCpuLimit}" +
            $"\n{"Avg CPU:",-18}{AvgCpu,8:F2}%   {"High CPU Count:",-26}{HighCpuCount,6}   {"Low CPU Count:",-26}{LowCpuCount,6}" +
            $"\n{"Avg Instance CPU:",-18}{AvgInstanceCpu,8:F2}%   {"High Instance CPU Count:",-26}{HighInstanceCpuCount,6}   {"Low Instance CPU Count:",-26}{LowInstanceCpuCount,6}" +
            $"\n{"Workers:",-18}{WorkersPercent,8:F2}%   {"High Worker Count:",-26}{HighWorkerCount,6}   {"Low Worker Count:",-26}{LowWorkerCount,6}" +
            $"\n{"Avg Data IO:",-18}{AvgDataIo,8:F2}%   {"High Data IO Count:",-26}{HighDataIoCount,6}   {"Low Data IO Count:",-26}{LowDataIoCount,6}" +
            $"\n{"Short Avg CPU:",-24}{ShortAvgCpu,8:F2}%   {"Long Avg CPU:",-24}{LongAvgCpu,8:F2}%" +
            $"\n{"Short Instance CPU:",-24}{ShortInstanceCpu,8:F2}%   {"Long Instance CPU:",-24}{LongInstanceCpu,8:F2}%" +
            $"\n{"Short Workers Percent:",-24}{ShortWorkersPercent,8:F2}%   {"Long Workers Percent:",-24}{LongWorkersPercent,8:F2}%" +
            $"\n{"Short Data IO:",-24}{ShortDataIo,8:F2}%   {"Long Data IO:",-24}{LongDataIo,8:F2}%";
    }
}
