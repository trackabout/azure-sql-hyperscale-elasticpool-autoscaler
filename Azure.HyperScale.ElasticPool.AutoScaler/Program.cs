using Azure.HyperScale.ElasticPool.AutoScaler;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry.Azure.Functions.Worker;

var isSentryLoggingEnabled = Convert.ToBoolean(Environment.GetEnvironmentVariable("IsSentryLoggingEnabled"));
var sentryDsn = Environment.GetEnvironmentVariable("SentryDsn");

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureFunctionsWorkerDefaults((context, builder) =>
    {
        if (isSentryLoggingEnabled && !string.IsNullOrEmpty(sentryDsn))
        {
            builder.UseSentry(context, options =>
            {
                options.Dsn = sentryDsn;
                options.Debug = false;
                options.TracesSampleRate = 0.0;     //Tracing is not required for this.
            });
        }
    })
    .ConfigureServices(services => {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddLogging();
        services.AddSingleton<AutoScalerConfiguration>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            return new AutoScalerConfiguration(configuration);
        });
        services.AddTransient<Azure.HyperScale.ElasticPool.AutoScaler.AutoScaler>();
    })
    .Build();

host.Run();

