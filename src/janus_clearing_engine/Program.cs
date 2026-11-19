using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Janus.Core.Shared.Extensions;
using Janus.ClearingEngine.Parsers;
using JanusClearingEngine.Infrastructure;
using JanusClearingEngine.Infrastructure.Channels;
using JanusClearingEngine.Repositories;
using JanusClearingEngine.Services;
using JanusClearingEngine.SignatureVerifiers;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((context, services) =>
{
    services.AddSingleton<FileTopologyManager>();
    services.AddSingleton<IFileSignatureVerifier, RsaFileSignatureVerifier>();
    services.AddSingleton<ClearingFileChannel>();

    services.AddDataBaseResilience(context.Configuration);

    services.AddScoped<InboundTransactionRepository>();

    var providerFormat = context.Configuration["ClearingProvider:Format"] ?? "EasyPay";
    services.AddScoped<IClearingParser, EasyPayClearingParser>();

    services.AddOpenTelemetry()
        .WithTracing(tracerProviderBuilder =>
        {
            tracerProviderBuilder
                .AddSource("JanusClearingEngine")
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("JanusClearingEngine"))
                .AddConsoleExporter();
        });

    services.AddHostedService<ClearingIngestionWorker>();
});

var host = builder.Build();
await host.RunAsync();
