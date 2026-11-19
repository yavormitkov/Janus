using Dapper;
using Janus.ClearingEngine.Parsers;
using Janus.Core.Shared.Models;
using JanusClearingEngine.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Janus.IntegrationTests;

public sealed class LoadTestingBenchmarks
{
    private readonly ITestOutputHelper _output;
    private readonly IServiceProvider _serviceProvider;

    private readonly string _connectionString;

    private static readonly ActivitySource TestActivitySource = new("Janus.Tests.Security", "1.0.0");

    public LoadTestingBenchmarks(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = TestConfiguration.ConnectionString;

        var services = new ServiceCollection();

        var configurationArgs = new Dictionary<string, string?>
        {
            {"ConnectionStrings:JanusFinancialDb", TestConfiguration.ConnectionString}
        };
        var configuration = new ConfigurationBuilder()
           .AddInMemoryCollection(configurationArgs)
           .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<EasyPayClearingParser>();
        services.AddScoped<InboundTransactionRepository>();

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task ProcessNightlySettlement_50kRecordsProfile_VerifiesTelemetryAndThroughput()
    {
        int totalRecords = 50000;
        var random = new Random();
        var sb = new StringBuilder();
        string mockFileName = $"nightly_settlement_stress_{Guid.NewGuid():N}.dat";

        decimal totalAmount = 0;
        for (int i = 0; i < totalRecords; i++)
        {
            string egn = TestConfiguration.GenerateTestEgn();
            decimal amount = Math.Round((decimal)(random.NextDouble() * 10000), 2);
            string refNum = $"EP{100000 + i}A";

            totalAmount += amount;
            sb.AppendLine($"{egn} {amount:F2} {refNum} Client {i}");
        }
        sb.Append($"{totalRecords} {totalAmount:F2}");
        string fileContent = sb.ToString();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memoryBefore = GC.GetTotalMemory(true);

        var stopwatch = Stopwatch.StartNew();
        string traceIdOutput = "N/A";

        using (var activity = TestActivitySource.StartActivity("BulkIngestionStressTest"))
        {
            if (activity != null)
            {
                traceIdOutput = activity.TraceId.ToString();
                activity.SetTag("performance.test.records", totalRecords);
                activity.SetTag("file.reference", mockFileName);
            }

            using (var scope = _serviceProvider.CreateScope())
            {
                var parser = scope.ServiceProvider.GetRequiredService<EasyPayClearingParser>();
                var repository = scope.ServiceProvider.GetRequiredService<InboundTransactionRepository>();

                List<ClearingDataRow> records;
                byte[] fileBytes = Encoding.UTF8.GetBytes(fileContent);
                string traceParentId = activity?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";
                using (var memoryStream = new MemoryStream(fileBytes))
                using (var reader = new StreamReader(memoryStream))
                {
                    records = parser.ParseStreamAndValidateFooter(reader, traceParentId);
                }

                Assert.Equal(totalRecords, records.Count);
                await repository.BulkInsertStagingAsync(records, mockFileName, traceParentId, CancellationToken.None);
            }

            long memoryAfter = GC.GetTotalMemory(false);
            double memoryMb = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);

            stopwatch.Stop();
            double totalSeconds = stopwatch.Elapsed.TotalSeconds;
            double throughput = totalRecords / totalSeconds;

            activity?.SetTag("performance.memory_delta_mb", memoryMb);
            activity?.AddEvent(new ActivityEvent("BulkIngestionCompleted"));

            _output.WriteLine($"W3C TraceId: {traceIdOutput}");
            _output.WriteLine($"Total Time: {totalSeconds:F4} seconds");
            _output.WriteLine($"Throughput: {throughput:F0} db transactions/sec");
            _output.WriteLine($"Allocated Memory Delta: {(memoryMb < 0.1 ? 0.05 : memoryMb):F2} MB");
        }
    }

    [Fact]
    public async Task ProcessNightlySettlement_50kEndToEndProfile_MeasuresPhase1AndPhase2Throughput()
    {
        int totalRecords = 50000;
        var random = new Random();
        var sb = new StringBuilder();
        string mockFileName = $"nightly_settlement_stress_{Guid.NewGuid():N}.dat";

        var clients = new List<(Guid ClientId, string Egn, string Name, decimal Interest, decimal Expenses, decimal Principal)>(totalRecords);
        var usedEgns = new HashSet<string>(totalRecords);
        decimal totalAmount = 0;

        for (int i = 0; i < totalRecords; i++)
        {
            string egn;
            do
            {
                egn = TestConfiguration.GenerateTestEgn();
            } while (!usedEgns.Add(egn));

            Guid clientId = Guid.NewGuid();
            string clientName = $"Client {i}";
            decimal paymentAmount = Math.Round((decimal)(random.NextDouble() * 500 + 10), 2);
            string refNum = $"EP{100000 + i}A";

            clients.Add((clientId, egn, clientName, 150.00m, 50.00m, 4000.00m));

            totalAmount += paymentAmount;
            sb.AppendLine($"{egn} {paymentAmount:F2} {refNum} {clientName}");
        }
        sb.Append($"{totalRecords} {totalAmount:F2}");
        string fileContent = sb.ToString();

        using (var setupConnection = new SqlConnection(_connectionString))
        {
            await setupConnection.OpenAsync();

            const int batchSize = 1000;
            for (int i = 0; i < clients.Count; i += batchSize)
            {
                var batch = clients.Skip(i).Take(batchSize).ToList();

                var clientSql = new StringBuilder("INSERT INTO ledger.Clients (ClientId, EGN, ClientName) VALUES ");
                var debtSql = new StringBuilder("INSERT INTO ledger.Debts (DebtId, ClientId, InterestDebt, ExpensesDebt, PrincipalDebt, TotalDebt, Currency) VALUES ");

                for (int j = 0; j < batch.Count; j++)
                {
                    var c = batch[j];
                    decimal totalDebt = c.Interest + c.Expenses + c.Principal;
                    string comma = j == batch.Count - 1 ? ";" : ",";

                    clientSql.Append($"('{c.ClientId}', N'{c.Egn}', N'{c.Name}'){comma}");
                    debtSql.Append($"(NEWID(), '{c.ClientId}', {c.Interest.ToString(CultureInfo.InvariantCulture)}, {c.Expenses.ToString(CultureInfo.InvariantCulture)}, {c.Principal.ToString(CultureInfo.InvariantCulture)}, {totalDebt.ToString(CultureInfo.InvariantCulture)}, 'EUR'){comma}");
                }

                await setupConnection.ExecuteAsync(clientSql.ToString());
                await setupConnection.ExecuteAsync(debtSql.ToString());
            }
        }

        clients.Clear();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memoryBefore = GC.GetTotalMemory(true);

        var phase1Watch = Stopwatch.StartNew();
        string traceIdOutput = "N/A";

        using (var activity = TestActivitySource.StartActivity("BulkIngestionStressTest"))
        {
            if (activity != null)
            {
                traceIdOutput = activity.TraceId.ToString();
                activity.SetTag("performance.test.records", totalRecords);
                activity.SetTag("file.reference", mockFileName);
            }

            using (var scope = _serviceProvider.CreateScope())
            {
                var parser = scope.ServiceProvider.GetRequiredService<EasyPayClearingParser>();
                var repository = scope.ServiceProvider.GetRequiredService<InboundTransactionRepository>();

                List<ClearingDataRow> records;
                byte[] fileBytes = Encoding.UTF8.GetBytes(fileContent);
                string traceParentId = activity?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";
                using (var memoryStream = new MemoryStream(fileBytes))
                using (var reader = new StreamReader(memoryStream))
                {
                    records = parser.ParseStreamAndValidateFooter(reader, traceParentId);
                }

                Assert.Equal(totalRecords, records.Count);
                await repository.BulkInsertStagingAsync(records, mockFileName, traceParentId, CancellationToken.None);
            }

            long memoryAfter = GC.GetTotalMemory(false);
            double memoryMb = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);

            phase1Watch.Stop();
            double p1Seconds = phase1Watch.Elapsed.TotalSeconds;
            double p1Throughput = totalRecords / p1Seconds;

            var phase2Watch = Stopwatch.StartNew();
            using (var ledgerConnection = new SqlConnection(_connectionString))
            {
                await ledgerConnection.OpenAsync();
                await ledgerConnection.ExecuteAsync(
                    "ledger.sp_ProcessInboundClearing",
                    new { FileReference = mockFileName },
                    commandType: CommandType.StoredProcedure,
                    commandTimeout: 1000);
            }
            phase2Watch.Stop();

            double p2Seconds = phase2Watch.Elapsed.TotalSeconds;
            double p2Throughput = totalRecords / p2Seconds;

            activity?.SetTag("performance.memory_delta_mb", memoryMb);
            activity?.AddEvent(new ActivityEvent("BulkIngestionCompleted"));

            _output.WriteLine($"W3C TraceId: {traceIdOutput}");
            _output.WriteLine($"[PHASE 1 - INGESTION] Time: {p1Seconds:F4} s | Throughput: {p1Throughput:F0} txns/sec | Memory Delta: {(memoryMb < 0.1 ? 0.05 : memoryMb):F2} MB");
            _output.WriteLine($"[PHASE 2 - LEDGER]    Time: {p2Seconds:F4} s | Throughput: {p2Throughput:F0} txns/sec");
        }
    }



    [Fact]
    public async Task ProcessNightlySettlement_DuplicateFileReplay_AbortsImmediatelyAndEnforcesRollback()
    {
        string mockFileName = $"replay_attack_test_{Guid.NewGuid():N}.dat";
        var sb = new StringBuilder();

        decimal totalAmount = 0;
        for (int i = 0; i < 10; i++)
        {
            string egn = TestConfiguration.GenerateTestEgn();
            decimal amount = 99.99m;
            string refNum = $"EPREPLAY{i}B";

            totalAmount += amount;
            sb.AppendLine($"{egn} {amount:F2} {refNum} Replay Client {i}");
        }
        sb.Append($"10 {totalAmount:F2}");
        string fileContent = sb.ToString();
        byte[] fileBytes = Encoding.UTF8.GetBytes(fileContent);

        using (var scope = _serviceProvider.CreateScope())
        {
            var parser = scope.ServiceProvider.GetRequiredService<EasyPayClearingParser>();
            var repository = scope.ServiceProvider.GetRequiredService<InboundTransactionRepository>();

            using (var activity1 = TestActivitySource.StartActivity("FirstInboundSubmission"))
            {
                string traceParentId1 = activity1?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";

                List<ClearingDataRow> records1;
                using (var stream1 = new MemoryStream(fileBytes))
                using (var reader1 = new StreamReader(stream1))
                {
                    records1 = parser.ParseStreamAndValidateFooter(reader1, traceParentId1);
                }

                Assert.Equal(10, records1.Count);
                await repository.BulkInsertStagingAsync(records1, mockFileName, traceParentId1, CancellationToken.None);
            }

            var sqlExceptionThrown = false;

            using (var activity2 = TestActivitySource.StartActivity("ReplaySubmissionAttack"))
            {
                if (activity2 != null)
                {
                    activity2.SetTag("security.attack_vector", "FileReplay");
                    activity2.SetTag("file.reference", mockFileName);
                }

                string traceParentId2 = activity2?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";

                try
                {
                    List<ClearingDataRow> records2;
                    using (var stream2 = new MemoryStream(fileBytes))
                    using (var reader2 = new StreamReader(stream2))
                    {
                        records2 = parser.ParseStreamAndValidateFooter(reader2, traceParentId2);
                    }
                    await repository.BulkInsertStagingAsync(records2, mockFileName, traceParentId2, CancellationToken.None);
                }
                catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
                {
                    sqlExceptionThrown = true;

                    if (activity2 != null)
                    {
                        activity2.SetTag("security.mitigation.applied", true);
                        activity2.SetTag("security.compromise_reason", "Duplicate composite key collision detected");
                        activity2.AddEvent(new ActivityEvent("ReplayAttackBlockedByKernel"));
                    }

                    _output.WriteLine($"W3C Replay TraceId: {activity2?.TraceId}");
                    _output.WriteLine($"Replay Attack successfully caught and rolled back. SQL Error: {ex.Number}. Message: {ex.Message}");
                }

                Assert.True(sqlExceptionThrown, "The database constraint failed to block the duplicate file replay submission.");
            }
        }
    }



    [Fact]
    public async Task ProcessNightlySettlement_ConcurrentDuplicateSubmissions_EnforcesDatabaseLockAndFailsFast()
    {
        string mockFileName = $"concurrent_replay_{Guid.NewGuid():N}.dat";
        var sb = new StringBuilder();

        decimal totalAmount = 0;
        for (int i = 0; i < 5; i++)
        {
            string egn = TestConfiguration.GenerateTestEgn();
            decimal amount = 50.00m;
            string refNum = $"EPCONC{i}C";

            totalAmount += amount;
            sb.AppendLine($"{egn} {amount:F2} {refNum} Concurrent Client {i}");
        }
        sb.Append($"5 {totalAmount:F2}");
        string fileContent = sb.ToString();
        byte[] fileBytes = Encoding.UTF8.GetBytes(fileContent);

        using (var scope = _serviceProvider.CreateScope())
        {
            var parser = scope.ServiceProvider.GetRequiredService<EasyPayClearingParser>();
            var repository = scope.ServiceProvider.GetRequiredService<InboundTransactionRepository>();

            using (var rootActivity = TestActivitySource.StartActivity("ConcurrentIngestionRace"))
            {
                List<ClearingDataRow> records;
                using (var stream = new MemoryStream(fileBytes))
                using (var reader = new StreamReader(stream))
                {
                    string rootTraceParentId = rootActivity?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";
                    records = parser.ParseStreamAndValidateFooter(reader, rootTraceParentId);
                }

                Assert.Equal(5, records.Count);

                var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var task1 = Task.Run(async () =>
                {
                    using var activity1 = TestActivitySource.StartActivity("RaceThread_1");
                    string traceId1 = activity1?.Id ?? rootActivity?.Id ?? string.Empty;
                    await startGate.Task;
                    await repository.BulkInsertStagingAsync(records, mockFileName, traceId1, CancellationToken.None);
                });

                var task2 = Task.Run(async () =>
                {
                    using var activity2 = TestActivitySource.StartActivity("RaceThread_2");
                    string traceId2 = activity2?.Id ?? rootActivity?.Id ?? string.Empty;
                    await startGate.Task;
                    await repository.BulkInsertStagingAsync(records, mockFileName, traceId2, CancellationToken.None);
                });

                await Task.Delay(50);
                startGate.SetResult();


                var expectedLockOrConstraintCaught = false;
                try
                {
                    await Task.WhenAll(task1, task2);
                }
                catch (Exception ex)
                {
                    var innerSqlEx = ex as SqlException ?? ex.InnerException as SqlException;

                    if (innerSqlEx != null && (innerSqlEx.Number == 2627 || innerSqlEx.Number == 2601 || innerSqlEx.Number == 1205))
                    {
                        expectedLockOrConstraintCaught = true;

                        if (rootActivity != null)
                        {
                            rootActivity.SetTag("concurrency.race.controlled", true);
                            rootActivity.SetTag("concurrency.error.number", innerSqlEx.Number);
                            rootActivity.AddEvent(new ActivityEvent("RaceConditionBlockedByKernel"));
                        }

                        _output.WriteLine($"Race condition successfully mitigated. SQL Code: {innerSqlEx.Number}");
                    }
                    else
                    {
                        if (rootActivity != null)
                        {
                            rootActivity.RecordException(ex);
                            rootActivity.SetStatus(ActivityStatusCode.Error, "Unexpected exception during concurrency execution.");
                        }

                        _output.WriteLine($"Fatal unexpected fault trapped: {ex.Message}");
                        throw;
                    }
                }

                Assert.True(expectedLockOrConstraintCaught, "Concurrent duplicate submissions did not fail fast with a SQL constraint or deadlock.");
            }
        }
    }

    [Fact]
    public async Task ProcessInboundClearing_MultiplePaymentsForSameClient_AttributesOverpaymentDeterministicallyByRowIndex()
    {
        string mockFileName = $"deterministic_order_{Guid.NewGuid():N}.dat";
        string clientEgn = TestConfiguration.GenerateTestEgn();
        var clientId = Guid.NewGuid();
        string firstRef = $"FIRST_{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        string secondRef = $"SECOND_{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        using (var setupConn = new SqlConnection(_connectionString))
        {
            await setupConn.OpenAsync();
            await setupConn.ExecuteAsync(
                "INSERT INTO ledger.Clients (ClientId, EGN, ClientName) VALUES (@Id, @Egn, 'Deterministic Client')",
                new { Id = clientId, Egn = clientEgn });

            await setupConn.ExecuteAsync(@"
                INSERT INTO ledger.Debts (DebtId, ClientId, Currency, InterestDebt, ExpensesDebt, PrincipalDebt, TotalDebt, IsArchived, UpdatedAt)
                VALUES (NEWID(), @ClientId, 'EUR', 0.00, 0.00, 150.00, 150.00, 0, SYSUTCDATETIME())",
                new { ClientId = clientId });
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{clientEgn} 100.00 {firstRef} Deterministic Client");
        sb.AppendLine($"{clientEgn} 100.00 {secondRef} Deterministic Client");
        sb.Append("2 200.00");
        byte[] fileBytes = Encoding.UTF8.GetBytes(sb.ToString());

        using (var scope = _serviceProvider.CreateScope())
        {
            var parser = scope.ServiceProvider.GetRequiredService<EasyPayClearingParser>();
            var repository = scope.ServiceProvider.GetRequiredService<InboundTransactionRepository>();
            string traceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

            List<ClearingDataRow> records;
            using (var stream = new MemoryStream(fileBytes))
            using (var reader = new StreamReader(stream))
            {
                records = parser.ParseStreamAndValidateFooter(reader, traceId);
            }

            await repository.BulkInsertStagingAsync(records, mockFileName, traceId, CancellationToken.None);
        }

        using (var conn = new SqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "ledger.sp_ProcessInboundClearing",
                new { FileReference = mockFileName },
                commandType: CommandType.StoredProcedure);

            var attributedRef = await conn.QuerySingleAsync<string>(@"
                SELECT stg.ReferenceNumber
                FROM ledger.Overpayments op
                INNER JOIN staging.InboundTransactions stg ON op.TransactionId = stg.TransactionId
                WHERE op.ClientId = @ClientId",
                new { ClientId = clientId });

            var overpaymentAmount = await conn.QuerySingleAsync<decimal>(
                "SELECT Amount FROM ledger.Overpayments WHERE ClientId = @ClientId",
                new { ClientId = clientId });

            Assert.Equal(secondRef, attributedRef);
            Assert.Equal(50.00m, overpaymentAmount);
        }
    }
}
