namespace Azure.HyperScale.ElasticPool.AutoScaler;

public record PoolTargetSettings(double VCore, double PerDbMaxCapacity, double PerDbMinCapacity = 0.0);