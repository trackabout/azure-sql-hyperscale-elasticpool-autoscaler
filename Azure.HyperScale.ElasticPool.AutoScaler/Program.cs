using Azure.HyperScale.ElasticPool.AutoScaler;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.ApplicationInsights;
using Sentry.Azure.Functions.Worker;

var sentryDsn = Environment.GetEnvironmentVariable("SentryDsn");
var isSentryLoggingEnabled = !string.IsNullOrEmpty(sentryDsn);

// The timer fires every 15 seconds, so a persistent fault repeats about 5,760 times a day.
// Send each distinct error at most once per window so one fault cannot exhaust the Sentry quota.
var sentryRepeatWindow = TimeSpan.FromMinutes(15);

var host = new HostBuilder()
    .ConfigureAppConfiguration((context, config) =>
    {
        if (context.HostingEnvironment.IsDevelopment())
        {
            config.AddUserSecrets(typeof(AutoScalerConfiguration).Assembly, optional: true);
        }
    })
    .ConfigureFunctionsWorkerDefaults((context, builder) =>
    {
        if (isSentryLoggingEnabled && !string.IsNullOrEmpty(sentryDsn))
        {
            builder.UseSentry(context, options =>
            {
                options.Dsn = sentryDsn;
                options.Debug = false;
                options.TracesSampleRate = 0.0;     //Tracing is not required for this.
                options.DefaultTags["SqlInstanceName"] = context.Configuration["SqlInstanceName"] ?? "unknown";
                var throttle = new SentryEventThrottle(sentryRepeatWindow, TimeProvider.System);
                options.SetBeforeSend((sentryEvent, _) => throttle.Filter(sentryEvent));
            });
        }
    })
    .ConfigureLogging(logging =>
    {
        // The worker sends only Warning and above to Application Insights. Let the
        // heartbeat's Information line through so an alert can watch for it.
        logging.AddFilter<ApplicationInsightsLoggerProvider>(typeof(RunHeartbeat).FullName, LogLevel.Information);
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
        services.AddSingleton<IAutoScalerConfiguration>(sp => sp.GetRequiredService<AutoScalerConfiguration>());
        services.AddSingleton<IErrorRecorder, ErrorRecorder>();
        services.AddSingleton<IRunHeartbeat, RunHeartbeat>();
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