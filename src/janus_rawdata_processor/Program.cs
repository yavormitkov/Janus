using Janus.Core.Shared.Extensions;
using JanusRawDataProcessor.Processors;
using JanusRawDataProcessor.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;


var builder = Host.CreateApplicationBuilder(args);

var raw = builder.Configuration.GetConnectionString("JanusFinancialDb") ?? throw new InvalidOperationException("Missing connection string.");
string connectionString = Environment.ExpandEnvironmentVariables(raw);

builder.Services.AddDataBaseResilience(builder.Configuration);

builder.Services.AddSingleton(new ClearingProcessRepository(connectionString));
builder.Services.AddHostedService<RawDataClearingProcessor>();

var host = builder.Build();
await host.RunAsync();
