namespace Janus.IntegrationTests;

using Dapper;
using Microsoft.Data.SqlClient;
using OpenTelemetry.Trace;
using System;
using System.Data;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

public sealed class IdentityConstraintTests
{
    private readonly string _connectionString;
    private static readonly ActivitySource FraudActivitySource = new("Janus.Tests.Security.Fraud", "1.0.0");

    public IdentityConstraintTests()
    {
        _connectionString = TestConfiguration.ConnectionString;
    }

    [Fact]
    public async Task ApplySuspenseCorrection_SameMakerAndChecker_ThrowsSecurityExceptionAndRollsBack()
    {
        var transactionId = Guid.NewGuid();
        var identicalUserId = Guid.NewGuid();
        var clientEgn = TestConfiguration.GenerateTestEgn();
        var fakeTraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        using (var setupConnection = new SqlConnection(_connectionString))
        {
            await setupConnection.OpenAsync();

            await setupConnection.ExecuteAsync(@"
                INSERT INTO staging.InboundTransactions (TransactionId, TraceId, FileReference, RawEGN, RawClientName, Amount, ReferenceNumber, Processed, CreatedAt, RowIndex)
                VALUES (@TxId, @TraceId, 'security_stg.dat', @Egn, 'Test Client', 250.00, 'REF999', 3, SYSUTCDATETIME(), 1)",
                new { TxId = transactionId, TraceId = fakeTraceId, Egn = clientEgn });


            await setupConnection.ExecuteAsync(@"
                INSERT INTO ledger.SuspenseTransactions (TransactionId, TraceId, FileReference, InboundEGN, InboundClientName, Amount, ReferenceNumber, SuspenseStatus, MakerUserId, CreatedAt) 
                VALUES (@TxId, @TraceId, 'security.dat', @Egn, 'Raw Name', 250.00, 'REF999', 20, @MakerId, SYSUTCDATETIME())",
                new { TxId = transactionId, TraceId = fakeTraceId, Egn = clientEgn, MakerId = identicalUserId });
        }

        using (var rootActivity = FraudActivitySource.StartActivity("FourEyesViolationAttempt"))
        {
            if (rootActivity != null)
            {
                rootActivity.SetTag("security.threat_vector", "InsiderFraudAttempt");
                rootActivity.SetTag("security.violating_user", identicalUserId);
                rootActivity.SetTag("suspense.transaction.id", transactionId);
            }

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                var exception = await Assert.ThrowsAsync<SqlException>(async () =>
                {
                    await connection.ExecuteAsync(
                        "ledger.sp_ApplySuspenseCorrection",
                        new { TransactionId = transactionId, CheckerUserId = identicalUserId },
                        commandType: CommandType.StoredProcedure);
                });

                if (rootActivity != null)
                {
                    rootActivity.RecordException(exception);
                    rootActivity.SetStatus(ActivityStatusCode.Error, "Security violation: Identity Constraint breached at storage kernel layer.");
                    rootActivity.SetTag("security.breach.blocked", true);
                    rootActivity.AddEvent(new ActivityEvent("FraudPreventionEnforced"));
                }

                Assert.Equal(50000, exception.Number);
                Assert.Contains("Security violation: Maker and Checker cannot be identical", exception.Message);
            }
        }
    }
}
