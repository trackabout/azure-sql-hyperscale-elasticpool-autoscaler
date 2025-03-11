using Azure.HyperScale.ElasticPool.AutoScaler;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry.Azure.Functions.Worker;

var isSentryLoggingEnabled = bool.TryParse(Environment.GetEnvironmentVariable("IsSentryLoggingEnabled"), out var parsedFlag)
    && parsedFlag;
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
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddLogging();
        services.AddSingleton<AutoScalerConfiguration>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            return new AutoScalerConfiguration(configuration);
        });
        services.AddSingleton<IErrorRecorder, ErrorRecorder>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ErrorRecorder>>();
            var autoScalerConfig = sp.GetRequiredService<AutoScalerConfiguration>();
            return new ErrorRecorder(logger, autoScalerConfig);
        });
        services.AddSingleton<ISqlRepository, SqlRepository>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SqlRepository>>();
            var autoScalerConfig = sp.GetRequiredService<AutoScalerConfiguration>();
            var errorRecorder = sp.GetRequiredService<IErrorRecorder>();
            return new SqlRepository(logger, autoScalerConfig, errorRecorder);
        });
        services.AddSingleton<IAzureResourceService, AzureResourceService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AzureResourceService>>();
            var autoScalerConfig = sp.GetRequiredService<AutoScalerConfiguration>();
            var errorRecorder = sp.GetRequiredService<IErrorRecorder>();
            var sqlRepository = sp.GetRequiredService<ISqlRepository>();
            return new AzureResourceService(autoScalerConfig, logger, errorRecorder, sqlRepository);
        });
        services.AddTransient<AutoScaler>();
    })
    .Build();

host.Run();