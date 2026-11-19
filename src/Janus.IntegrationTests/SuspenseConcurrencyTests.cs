namespace Janus.IntegrationTests;

using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Dapper;
using Xunit;

public sealed class SuspenseConcurrencyTests
{
    private static readonly ActivitySource ConcurrencySource = new("Janus.Tests.Concurrency.Suspense", "1.0.0");

    [Fact]
    public async Task ApplySuspenseCorrection_ConcurrentThreads_EnforcesExclusiveLockAndFailsFast()
    {
        var connectionString = TestConfiguration.ConnectionString;
        var transactionId = Guid.NewGuid();
        var makerId = Guid.NewGuid();
        var checkerId1 = Guid.NewGuid();
        var checkerId2 = Guid.NewGuid();
        var clientEgn = TestConfiguration.GenerateTestEgn();

        using var rootActivity = ConcurrencySource.StartActivity("SuspenseRaceConditionRoot");
        string baseTraceId = rootActivity?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";

        using (var setupConnection = new SqlConnection(connectionString))
        {
            await setupConnection.OpenAsync();

            var targetClientId = Guid.NewGuid();

            await setupConnection.ExecuteAsync(
                "INSERT INTO ledger.Clients (ClientId, EGN, ClientName) VALUES (@Id, @Egn, 'Test Client')",
                new { Id = targetClientId, Egn = clientEgn });

            await setupConnection.ExecuteAsync(@"
                INSERT INTO ledger.Debts (DebtId, ClientId, Currency, InterestDebt, ExpensesDebt, PrincipalDebt, TotalDebt, IsArchived, UpdatedAt) 
                VALUES (@DebtId, @ClientId, 'EUR', 10.00, 0.00, 90.00, 100.00, 0, SYSUTCDATETIME())",
                new { DebtId = Guid.NewGuid(), ClientId = targetClientId, Total = 100.00m });

            await setupConnection.ExecuteAsync(@"
                INSERT INTO staging.InboundTransactions (TransactionId, TraceId, FileReference, RawEGN, RawClientName, Amount, ReferenceNumber, Processed, CreatedAt, RowIndex)
                VALUES (@TxId, @TraceId, 'demo_stg.dat', '0000000000', 'Raw Name', 100.00, 'REF123', 3, SYSUTCDATETIME(), 1)",
                new { TxId = transactionId, TraceId = baseTraceId });

            await setupConnection.ExecuteAsync(@"
                INSERT INTO ledger.SuspenseTransactions (TransactionId, FileReference, InboundEGN, InboundClientName, CorrectedEGN, Amount, ReferenceNumber, SuspenseStatus, MakerUserId, TraceId, CreatedAt) 
                VALUES (@TxId, 'demo.dat', '0000000000', 'Raw Name', @CorrectedEgn, 100.00, 'REF123', 20, @MakerId, @TraceId, SYSUTCDATETIME())",
                new { TxId = transactionId, CorrectedEgn = clientEgn, MakerId = makerId, TraceId = baseTraceId });
        }


        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = Task.Run(() => ExecuteApprovalAsync(connectionString, transactionId, checkerId1, startGate));
        var task2 = Task.Run(() => ExecuteApprovalAsync(connectionString, transactionId, checkerId2, startGate));

        await Task.Delay(50);
        startGate.SetResult();

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(r => r == null);
        var failureCount = results.Count(r => r != null);

        if (rootActivity != null)
        {
            rootActivity.SetTag("concurrency.success_count", successCount);
            rootActivity.SetTag("concurrency.failure_count", failureCount);
            rootActivity.AddEvent(new ActivityEvent("SuspenseRaceConditionEvaluationCompleted"));
        }

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);
    }

    private static async Task<Exception?> ExecuteApprovalAsync(string connectionString, Guid transactionId, Guid checkerId, TaskCompletionSource startGate)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await startGate.Task;

            await connection.ExecuteAsync(
                "ledger.sp_ApplySuspenseCorrection",
                new { TransactionId = transactionId, CheckerUserId = checkerId },
                commandType: CommandType.StoredProcedure);

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

}
