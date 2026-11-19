using Janus.ClearingEngine.Parsers;
using Janus.Core.Shared.Models;
using JanusClearingEngine.Infrastructure;
using JanusClearingEngine.Infrastructure.Channels;
using JanusClearingEngine.Repositories;
using JanusClearingEngine.SignatureVerifiers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Polly;
using Polly.Registry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace JanusClearingEngine.Services;

public sealed class ClearingIngestionWorker : BackgroundService
{
    private readonly FileTopologyManager _topologyManager;
    private readonly IFileSignatureVerifier _signatureVerifier;
    private readonly ClearingFileChannel _fileChannel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ResiliencePipeline _retryPipeline;
    private static readonly ActivitySource ActivitySource = new("JanusClearingEngine");

    public ClearingIngestionWorker(
        FileTopologyManager topologyManager,
        IFileSignatureVerifier signatureVerifier,
        ClearingFileChannel fileChannel,
        IServiceProvider serviceProvider,
        ResiliencePipelineProvider<string> pipelineProvider,
        IOptions<ResilienceOptions> resilienceOptions)
    {
        _topologyManager = topologyManager ?? throw new ArgumentNullException(nameof(topologyManager));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _fileChannel = fileChannel ?? throw new ArgumentNullException(nameof(fileChannel));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _retryPipeline = pipelineProvider.GetPipeline(resilienceOptions.Value.PipelineName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _topologyManager.OnFileDropped += (filePath) => _ = _fileChannel.Writer.WriteAsync(filePath, stoppingToken);
        _topologyManager.InitializeWatcher();

        foreach (var recoveredFile in _topologyManager.GetCrashRecoveredWorkingFiles())
        {
            await _fileChannel.Writer.WriteAsync(recoveredFile, stoppingToken);
        }

        string? existingFile;
        while (!string.IsNullOrEmpty(existingFile = _topologyManager.GetNextAvailableFile()))
        {
            await _fileChannel.Writer.WriteAsync(existingFile, stoppingToken);
        }

        try
        {
            await foreach (var rawFilePath in _fileChannel.Reader.ReadAllAsync(stoppingToken))
            {
                string filePath = _topologyManager.EnsureInWorking(rawFilePath);
                string fileReference = Path.GetFileName(filePath);
                string signaturePath = filePath + ".sign";

                using var activity = ActivitySource.StartActivity("ProcessClearingFile");
                activity?.SetTag("file.path", filePath);
                activity?.SetTag("file.name", fileReference);

                using var scope = _serviceProvider.CreateScope();
                var parser = scope.ServiceProvider.GetRequiredService<IClearingParser>();
                var repository = scope.ServiceProvider.GetRequiredService<InboundTransactionRepository>();

                try
                {
                    if (!File.Exists(signaturePath))
                    {
                        throw new SecurityException($"Missing cryptographic signature file for: {filePath}");
                    }

                    byte[] signatureData = await File.ReadAllBytesAsync(signaturePath, stoppingToken);
                    string fullTraceParent = activity?.Id ?? Activity.Current?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";

                    List<ClearingDataRow> records;
                    using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
                    {
                        activity?.AddEvent(new ActivityEvent("VerifyingSignature"));
                        _signatureVerifier.VerifySignature(fileStream, signatureData);

                        fileStream.Position = 0;

                        activity?.AddEvent(new ActivityEvent("ParsingStream"));
                        using (var reader = new StreamReader(fileStream))
                        {
                            records = parser.ParseStreamAndValidateFooter(reader, fullTraceParent);
                        }
                    }

                    activity?.SetTag("records.count", records.Count);
                    activity?.AddEvent(new ActivityEvent("BulkInsertingToStaging"));
                    activity?.SetTag("records.count", records.Count);
                    activity?.AddEvent(new ActivityEvent("BulkInsertingToStaging"));

                    await _retryPipeline.ExecuteAsync(
                        async token => await repository.BulkInsertStagingAsync(records, fileReference, fullTraceParent, token),
                        stoppingToken);

                    _topologyManager.MoveToOk(filePath);
                    _topologyManager.MoveToOk(signaturePath);

                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (SecurityException ex)
                {
                    var traceId = activity?.TraceId.ToString().ToLowerInvariant();
                    RecordActivityError(activity, "SECURITY BREACH", ex);
                    HandleFault(filePath, signaturePath, "SECURITY BREACH", ex.Message, ex.StackTrace, null, traceId);
                }
                catch (FormatException ex)
                {
                    var traceId = activity?.TraceId.ToString().ToLowerInvariant();
                    RecordActivityError(activity, "FORMAT VIOLATION", ex);
                    HandleFault(filePath, signaturePath, "FORMAT VIOLATION", ex.Message, ex.StackTrace, null, traceId);
                }
                catch (SqlException ex)
                {
                    var traceId = activity?.TraceId.ToString().ToLowerInvariant();
                    RecordActivityError(activity, "DATABASE VIOLATION", ex);
                    activity?.SetTag("db.error_code", ex.Number);
                    HandleFault(filePath, signaturePath, "DATABASE VIOLATION", ex.Message, ex.StackTrace, ex.Number, traceId);
                }
                catch (Exception ex)
                {
                    var traceId = activity?.TraceId.ToString().ToLowerInvariant();
                    RecordActivityError(activity, "SYSTEM FAULT", ex);
                    HandleFault(filePath, signaturePath, "SYSTEM FAULT", ex.Message, ex.StackTrace, null, traceId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[INGESTION SHUTDOWN] Inbound edge discovery worker gracefully terminated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC.");
        }
        finally
        {
            _topologyManager.DisposeWatcher();
        }
    }

    private void HandleFault(string filePath, string signaturePath, string faultType, string message, string? stackTrace, int? sqlErrorCode, string? traceId)
    {
        string errorDetails;
        string displayTraceId = string.IsNullOrEmpty(traceId) ? "N/A" : traceId;

        if (sqlErrorCode.HasValue)
        {
            errorDetails = $"[{faultType}]\nTraceId: {displayTraceId}\nTimestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\nSQL Error: {sqlErrorCode.Value}\nMessage: {message}\n\nStack Trace:\n{stackTrace}";
        }
        else
        {
            errorDetails = $"[{faultType}]\nTraceId: {displayTraceId}\nTimestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\nMessage: {message}\n\nStack Trace:\n{stackTrace}";
        }

        _topologyManager.WriteErrorLog(filePath, errorDetails);
        _topologyManager.MoveToError(filePath);
        if (File.Exists(signaturePath))
        {
            _topologyManager.MoveToError(signaturePath);
        }
    }

    private static void RecordActivityError(Activity? activity, string faultType, Exception ex)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("fault.type", faultType);
        activity.RecordException(ex);
    }
}
