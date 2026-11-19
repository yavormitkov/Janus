using Janus.Core.Shared.Events;
using Janus.Core.Shared.Models;
using JanusMessageProcessor.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using System.Diagnostics;
using System.Text.Json;

namespace JanusMessageProcessor.Processors;

public sealed class OutboxEventProcessor : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("JanusMessageProcessor");

    private readonly OutboxEventRepository _repository;
    private readonly ResiliencePipeline _retryPipeline;
    private readonly ILogger<OutboxEventProcessor> _logger;
    private readonly TimeSpan _interval;

    public OutboxEventProcessor(
        IConfiguration configuration,
        OutboxEventRepository repository,
        ResiliencePipelineProvider<string> pipelineProvider,
        IOptions<ResilienceOptions> resilienceOptions,
        ILogger<OutboxEventProcessor> logger)
    {
        _repository = repository;
        _logger = logger;
        _retryPipeline = pipelineProvider.GetPipeline(resilienceOptions.Value.PipelineName);

        var pollingInterval = configuration.GetSection("OutboxPolicy")["PollingIntervalMilliseconds"];
        int intervalMs = int.TryParse(pollingInterval, out var parsed) && parsed > 0 ? parsed : 10000;
        _interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[START OUTBOX] Processor started. Polling database every {IntervalMs} ms.", _interval.TotalMilliseconds);

        try
        {
            await ProcessPendingEventsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ERROR OUTBOX] Immediate initial process fault.");
        }

        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessPendingEventsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ERROR OUTBOX] Critical error in processing loop.");
            }
        }

        _logger.LogInformation("[SHUTDOWN OUTBOX] Processor gracefully terminated.");
    }

    private async Task ProcessPendingEventsAsync(CancellationToken stoppingToken)
    {
        var pendingEvents = await _retryPipeline.ExecuteAsync(
            async token => await _repository.GetAndProcessPendingOutboxEventsAsync(token),
            stoppingToken);

        foreach (var outboxEvent in pendingEvents)
        {
            Activity? activity = !string.IsNullOrEmpty(outboxEvent.TraceId) &&
                                 ActivityContext.TryParse(outboxEvent.TraceId, null, out var parentContext)
                ? ActivitySource.StartActivity("PublishOutboxEvent", ActivityKind.Consumer, parentContext)
                : ActivitySource.StartActivity("PublishOutboxEvent");

            using (activity)
            {
                string displayTraceId = activity?.TraceId.ToString().ToLowerInvariant()
                                        ?? Guid.NewGuid().ToString("N").ToLowerInvariant();

                try
                {
                    var eventData = JsonSerializer.Deserialize<OverpaymentDetectedEvent>(outboxEvent.Payload);

                    if (eventData != null)
                    {
                        activity?.SetTag("client.id", eventData.ClientId);
                        activity?.SetTag("file.reference", eventData.FileReference);

                        _logger.LogInformation(
                            "[AUDIT OUTBOX] [TraceId: {TraceId}] Overpayment tracked for Client {ClientId}. File: {FileReference}. Reference: {ReferenceNumber}. Excess Amount: {OverpaidAmount} EUR.",
                            displayTraceId,
                            eventData.ClientId,
                            eventData.FileReference,
                            eventData.ReferenceNumber,
                            eventData.OverpaidAmount);
                    }

                    await _retryPipeline.ExecuteAsync(
                        async token => await _repository.UpdateEventStatusAsync(outboxEvent.EventId, 1, token),
                        stoppingToken);

                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (JsonException ex)
                {
                    if (activity != null)
                    {
                        activity.SetTag("outbox.outcome", "POISON_ISOLATED");
                        var eventTags = new ActivityTagsCollection { { "poison.event.id", outboxEvent.EventId } };
                        activity.AddEvent(new ActivityEvent("PoisonMessageTrappedAndIsolated", DateTime.UtcNow, eventTags));
                    }

                    await _retryPipeline.ExecuteAsync(
                        async token => await _repository.UpdateEventStatusAsync(outboxEvent.EventId, -1, token),
                        stoppingToken);

                    HandleEventFault(activity, ex, displayTraceId);
                }
                catch (Exception ex)
                {
                    HandleEventFault(activity, ex, displayTraceId);
                }
            }
        }
    }

    private void HandleEventFault(Activity? activity, Exception ex, string traceId)
    {
        if (activity != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity.SetTag("fault.type", "OUTBOX_PROCESSING_FAULT");
        }

        _logger.LogError(ex, "[ERROR OUTBOX] [TraceId: {TraceId}] Fault processing event.", traceId);
    }
}
