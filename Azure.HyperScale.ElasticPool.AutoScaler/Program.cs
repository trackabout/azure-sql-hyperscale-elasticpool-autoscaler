using Azure.HyperScale.ElasticPool.AutoScaler;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
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