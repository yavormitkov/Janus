using Janus.Core.Shared.Models;
using JanusRawDataProcessor.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace JanusRawDataProcessor.Processors;

public sealed class RawDataClearingProcessor : BackgroundService
{
    private readonly ClearingProcessRepository _repository;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly TimeSpan _interval;
    private readonly ResiliencePipeline _retryPipeline;

    private static readonly ActivitySource ActivitySource = new("JanusRawDataProcessor");

    public RawDataClearingProcessor(
                                ClearingProcessRepository repository, 
                                IHostApplicationLifetime appLifetime, 
                                IConfiguration configuration, 
                                ResiliencePipelineProvider<string> pipelineProvider,
                                IOptions<ResilienceOptions> resilienceOptions)
    {
        _repository = repository;
        _appLifetime = appLifetime;
        var pollingInterval = configuration.GetSection("OutboxPolicy")["PollingIntervalMilliseconds"];
        _interval = TimeSpan.FromMilliseconds(string.IsNullOrEmpty(pollingInterval) ? 10000 : int.Parse(pollingInterval));
        _retryPipeline = pipelineProvider.GetPipeline(resilienceOptions.Value.PipelineName);
    }


    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"[START RAWDATA] Processor started. Polling database every {_interval} ms.");
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    await ProcessPendingFilesAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR RAWDATA] Critical error in processing loop: {ex.Message}");
            }          
        }

        Console.WriteLine("[SHUTDOWN RAWDATA] Processor gracefully terminated.");
    }

    private async Task ProcessPendingFilesAsync(CancellationToken stoppingToken)
    {
        var pendingFiles = await _retryPipeline.ExecuteAsync(async token =>
        {
            return await _repository.GetPendingFilesAsync();
        }, stoppingToken);

        foreach (var file in pendingFiles)
        {
            string? savedTraceParent = file.TraceId;

            Activity? activity = null;

            if (!string.IsNullOrEmpty(savedTraceParent) && ActivityContext.TryParse(savedTraceParent, null, out var parentContext))
            {
                activity = ActivitySource.StartActivity("ProcessInboundClearing", ActivityKind.Consumer, parentContext);
            }
            else
            {
                activity = ActivitySource.StartActivity("ProcessInboundClearing");
            }
            using (activity)
            {
                string displayTraceId = activity?.TraceId.ToString().ToLowerInvariant() ?? Guid.NewGuid().ToString("N").ToLowerInvariant();

                activity?.SetTag("file.reference", file.FileName);

                try
                {
                    activity?.AddEvent(new ActivityEvent("StartingDatabaseProcessing"));
                    await _repository.ProcessInboundClearingAsync(file.FileName);

                    activity?.SetStatus(ActivityStatusCode.Ok);

                    Console.WriteLine($"[AUDIT RAWDATA] [TraceId: {displayTraceId}] File {file.FileName} finalized and registered successfully.");
                }
                catch (SqlException ex)
                {
                    RecordActivityError(activity, "DATABASE_FAULT", ex);
                    Console.WriteLine($"[ERROR RAWDATA] [TraceId: {displayTraceId}] Database fault for batch {file.FileName}. Code: {ex.Number}. Message: {ex.Message}");
                }
                catch (Exception ex)
                {
                    RecordActivityError(activity, "UNEXPECTED_FAULT", ex);
                    Console.WriteLine($"[ERROR RAWDATA] [TraceId: {displayTraceId}] Unexpected fault for batch {file.FileName}. Message: {ex.Message}");
                }
            }
        }
    }

    private static void RecordActivityError(Activity? activity, string faultType, Exception ex)
    {
        if (activity == null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("fault.type", faultType);
    }
}
