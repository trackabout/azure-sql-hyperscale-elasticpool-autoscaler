namespace Azure.HyperScale.ElasticPool.AutoScaler;

public interface IErrorRecorder
{
    void RecordError(string message);
    void RecordError(Exception exception, string message);
}