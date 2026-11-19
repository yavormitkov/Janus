using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Janus.Core.Shared.Extensions;
using JanusMessageProcessor.Processors;
using JanusMessageProcessor.Repositories;


var builder = Host.CreateApplicationBuilder(args);

var raw = builder.Configuration.GetConnectionString("JanusFinancialDb") ?? throw new InvalidOperationException("Missing connection string.");
string connectionString = Environment.ExpandEnvironmentVariables(raw);


builder.Services.AddDataBaseResilience(builder.Configuration);

builder.Services.AddSingleton(new OutboxEventRepository(connectionString));
builder.Services.AddHostedService<OutboxEventProcessor>();

var host = builder.Build();
await host.RunAsync();
