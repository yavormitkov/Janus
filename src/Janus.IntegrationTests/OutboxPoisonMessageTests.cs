namespace Janus.IntegrationTests;

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;
using Dapper;
using JanusMessageProcessor.Repositories;
using JanusMessageProcessor.Processors;
using System.Threading;
using System.Collections.Generic;

public sealed class OutboxPoisonMessageTests
{
    private readonly string _connectionString;
    private readonly IServiceProvider _serviceProvider;
    private static readonly ActivitySource OutboxSource = new("Janus.Tests.Outbox.Poison", "1.0.0");

    public OutboxPoisonMessageTests()
    {
        _connectionString = TestConfiguration.ConnectionString;

        var services = new ServiceCollection();
        var configurationArgs = new Dictionary<string, string?>
        {
            {"ConnectionStrings:JanusFinancialDb", _connectionString},
            {"OutboxPolicy:PollingIntervalMilliseconds", "100"}
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationArgs)
            .Build();

        var mockPipelineProvider = new MockResiliencePipelineProvider(Polly.ResiliencePipeline.Empty);

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<Polly.Registry.ResiliencePipelineProvider<string>>(mockPipelineProvider);
        services.AddScoped<OutboxEventRepository>(sp => new OutboxEventRepository(_connectionString));
        services.AddScoped<OutboxEventProcessor>();

        _serviceProvider = services.BuildServiceProvider();
    }

    private sealed class MockResiliencePipelineProvider : Polly.Registry.ResiliencePipelineProvider<string>
    {
        private readonly Polly.ResiliencePipeline _pipeline;

        public MockResiliencePipelineProvider(Polly.ResiliencePipeline pipeline)
        {
            _pipeline = pipeline;
        }

        public override Polly.ResiliencePipeline GetPipeline(string key)
        {
            return _pipeline;
        }

        public override bool TryGetPipeline(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Polly.ResiliencePipeline? pipeline)
        {
            pipeline = _pipeline;
            return true;
        }

        public override bool TryGetPipeline<TResult>(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Polly.ResiliencePipeline<TResult>? pipeline)
        {
            pipeline = Polly.ResiliencePipeline<TResult>.Empty;
            return true;
        }
    }

    [Fact]
    public async Task ProcessOutboxQueue_ContainsPoisonMessage_IsolatesToDeadLetterAndProcessesNext()
    {
        var poisonEventId = Guid.NewGuid();
        var validEventId = Guid.NewGuid();
        var mockFile = "poison_test.dat";
        string poisonTraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        string validTraceId = "00-9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c-0f1e2d3c4b5a6f7e-01";

        using (var setupConnection = new SqlConnection(_connectionString))
        {
            await setupConnection.OpenAsync();

            await setupConnection.ExecuteAsync("DELETE FROM ledger.OutboxEvents WHERE IsProcessed = 0");

            await setupConnection.ExecuteAsync(
                "INSERT INTO ledger.OutboxEvents (EventId, EventType, Payload, TraceId, IsProcessed, CreatedAt) " +
                "VALUES (@EventId, 'OverpaymentDetectedEvent', '{ invalid-json-payload-trap : ', @TraceId, 0, SYSUTCDATETIME())",
                new { EventId = poisonEventId, TraceId = poisonTraceId });

            var validPayloadObject = new Dictionary<string, object>
            {
                { "ClientId", Guid.NewGuid() },
                { "OriginalAmount", 100.00m },
                { "OverpaidAmount", 50.00m },
                { "ReferenceNumber", "EPPOISON1A" },
                { "FileReference", mockFile }
            };
            var validPayload = JsonSerializer.Serialize(validPayloadObject);

            await setupConnection.ExecuteAsync(
                "INSERT INTO ledger.OutboxEvents (EventId, EventType, Payload, TraceId, IsProcessed, CreatedAt) " +
                "VALUES (@EventId, 'OverpaymentDetectedEvent', @Payload, @TraceId, 0, SYSUTCDATETIME())",
                new { EventId = validEventId, Payload = validPayload, TraceId = validTraceId });
        }

        using (var rootActivity = OutboxSource.StartActivity("OutboxPoisonMitigationRun"))
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var processor = scope.ServiceProvider.GetRequiredService<OutboxEventProcessor>();
                using var cts = new CancellationTokenSource();

                var processorTask = processor.StartAsync(cts.Token);
                await Task.Delay(150);
                await cts.CancelAsync();

                try
                {
                    await processorTask;
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
                {
                    rootActivity?.AddEvent(new ActivityEvent("ProcessorGracefullyCanceledByTest"));
                }
            }
        }

        using (var verifyConnection = new SqlConnection(_connectionString))
        {
            await verifyConnection.OpenAsync();

            var poisonStatus = await verifyConnection.QuerySingleAsync<int>(
                "SELECT IsProcessed FROM ledger.OutboxEvents WHERE EventId = @EventId",
                new { EventId = poisonEventId });

            var validStatus = await verifyConnection.QuerySingleAsync<int>(
                "SELECT IsProcessed FROM ledger.OutboxEvents WHERE EventId = @EventId",
                new { EventId = validEventId });

            Assert.Equal(-1, poisonStatus);
            Assert.Equal(1, validStatus);
        }
    }
}
