namespace Azure.HyperScale.ElasticPool.AutoScaler;

public class PoolTargetSettings(double targetVCore, double perDbMaxCapacity)
{
    public double VCore { get; } = targetVCore;

    public const double PerDbMinCapacity = 0;

    public double PerDbMaxCapacity => perDbMaxCapacity;
}