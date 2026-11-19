namespace Janus.IntegrationTests;

using Janus.ClearingEngine.Parsers;
using Janus.Core.Shared.Models;
using JanusClearingEngine.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Dapper;
using System;
using System.Data;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public sealed class IdempotencyTests
{
    private readonly string _connectionString;
    private readonly IServiceProvider _serviceProvider;
    private static readonly ActivitySource IdempotencyActivitySource = new("Janus.Tests.Idempotency", "1.0.0");

    public IdempotencyTests()
    {
        _connectionString = TestConfiguration.ConnectionString;

        var services = new ServiceCollection();
        var configurationArgs = new Dictionary<string, string?>
        {
            {"ConnectionStrings:JanusFinancialDb", _connectionString}
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
    public async Task ProcessInboundClearing_SequentialDuplicateCallsWithRealBulkStaging_BypassesSecondSafelyWithoutDuplicateSideEffects()
    {
        var fileReference = $"idempotency_bulk_test_{Guid.NewGuid():N}.dat";
        using var rootActivity = IdempotencyActivitySource.StartActivity("IdempotencyRealBulkFlowTest");
        string fullTraceParent = rootActivity?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";

        var clientId = Guid.NewGuid();
        string validEgn = TestConfiguration.GenerateTestEgn();
        string invalidEgn = "9999999999";
        string validRef = $"REF_{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        string suspRef = $"SUSP_{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        using (var setupConn = new SqlConnection(_connectionString))
        {
            await setupConn.OpenAsync();
            await setupConn.ExecuteAsync(
                "INSERT INTO ledger.Clients (ClientId, EGN, ClientName) VALUES (@Id, @Egn, 'Idempotency Client')",
                new { Id = clientId, Egn = validEgn });

            await setupConn.ExecuteAsync(@"
                INSERT INTO ledger.Debts (DebtId, ClientId, Currency, InterestDebt, ExpensesDebt, PrincipalDebt, TotalDebt, IsArchived, UpdatedAt)
                VALUES (NEWID(), @ClientId, 'EUR', 0.00, 0.00, 100.00, 100.00, 0, SYSUTCDATETIME())",
                new { ClientId = clientId });
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{validEgn} 150.00 {validRef} Client Valid");
        sb.AppendLine($"{invalidEgn} 200.00 {suspRef} Client Suspense");
        sb.Append("2 350.00");
        string fileContent = sb.ToString();

        using (var scope = _serviceProvider.CreateScope())
        {
            var parser = scope.ServiceProvider.GetRequiredService<EasyPayClearingParser>();
            var repository = scope.ServiceProvider.GetRequiredService<InboundTransactionRepository>();

            List<ClearingDataRow> records;
            byte[] fileBytes = Encoding.UTF8.GetBytes(fileContent);
            using (var memoryStream = new MemoryStream(fileBytes))
            using (var reader = new StreamReader(memoryStream))
            {
                records = parser.ParseStreamAndValidateFooter(reader, fullTraceParent);
            }

            Assert.Equal(2, records.Count);
            await repository.BulkInsertStagingAsync(records, fileReference, fullTraceParent, CancellationToken.None);
        }

        using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();

            var firstException = await Record.ExceptionAsync(async () =>
            {
                await connection.ExecuteAsync(
                    "ledger.sp_ProcessInboundClearing",
                    new { FileReference = fileReference },
                    commandType: CommandType.StoredProcedure);
            });
            Assert.Null(firstException);

            var secondException = await Record.ExceptionAsync(async () =>
            {
                await connection.ExecuteAsync(
                    "ledger.sp_ProcessInboundClearing",
                    new { FileReference = fileReference },
                    commandType: CommandType.StoredProcedure);
            });
            Assert.Null(secondException);

            int registryCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM ledger.IdempotencyRegistry WHERE ReferenceNumber = @Ref",
                new { Ref = validRef });

            int overpaymentCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM ledger.Overpayments WHERE ClientId = @ClientId",
                new { ClientId = clientId });

            int outboxCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM ledger.OutboxEvents WHERE Payload LIKE @ClientPattern",
                new { ClientPattern = $"%{clientId}%" });

            Assert.Equal(1, registryCount);
            Assert.Equal(1, overpaymentCount);
            Assert.Equal(1, outboxCount);
        }
    }
}
