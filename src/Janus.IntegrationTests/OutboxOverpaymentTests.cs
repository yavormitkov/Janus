namespace Janus.IntegrationTests;

using Dapper;
using Janus.ClearingEngine.Parsers;
using Janus.Core.Shared.Events;
using Janus.Core.Shared.Models;
using JanusClearingEngine.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

public sealed class OutboxOverpaymentTests
{
    private readonly string _connectionString;
    private readonly IServiceProvider _serviceProvider;
    private readonly ITestOutputHelper _output;
    private static readonly ActivitySource ProcessingActivitySource = new("Janus.Tests.Processing", "1.0.0");

    public OutboxOverpaymentTests(ITestOutputHelper output)
    {
        _output = output;
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
    public async Task ProcessClearingQueue_OverpaymentOfExistingDebt_WritesOutboxEventWithCorrectExcessAmount()
    {
        var originalCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        var originalUiCulture = System.Threading.Thread.CurrentThread.CurrentUICulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

        try
        {
            var fileReference = $"overpayment_outbox_{Guid.NewGuid():N}.dat";
            string clientEgn = TestConfiguration.GenerateTestEgn();
            string referenceNumber = $"EP{Guid.NewGuid().ToString()[..4].ToUpper()}A";
            string clientName = "Test Overpayer";
            var clientId = Guid.NewGuid();
            var debtId = Guid.NewGuid();

            decimal debtTotal = 50.00m;
            decimal paymentAmount = 150.00m;
            decimal expectedExcess = paymentAmount - debtTotal; // 100.00m

            using (var setupConnection = new SqlConnection(_connectionString))
            {
                await setupConnection.OpenAsync();

                await setupConnection.ExecuteAsync(@"
                INSERT INTO ledger.Clients (ClientId, EGN, ClientName) 
                VALUES (@Id, @Egn, @Name)",
                    new { Id = clientId, Egn = clientEgn, Name = clientName });

                await setupConnection.ExecuteAsync(@"
                INSERT INTO ledger.Debts (DebtId, ClientId, Currency, InterestDebt, ExpensesDebt, PrincipalDebt, TotalDebt, IsArchived, UpdatedAt) 
                VALUES (@DebtId, @ClientId, 'EUR', 10.00, 0.00, 40.00, @Total, 0, SYSUTCDATETIME())",
                    new { DebtId = debtId, ClientId = clientId, Total = debtTotal });
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{clientEgn} {paymentAmount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} {referenceNumber} {clientName}");
            sb.Append($"1 {paymentAmount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            string fileContent = sb.ToString();
            byte[] fileBytes = Encoding.UTF8.GetBytes(fileContent);

            using (var rootActivity = ProcessingActivitySource.StartActivity("EndToEndOverpaymentFlow"))
            {
                string rootTraceParentId = rootActivity?.Id ?? "00-00000000000000000000000000000000-0000000000000000-00";

                using (var scope = _serviceProvider.CreateScope())
                {
                    var parser = scope.ServiceProvider.GetRequiredService<EasyPayClearingParser>();
                    var repository = scope.ServiceProvider.GetRequiredService<InboundTransactionRepository>();

                    List<ClearingDataRow> records;
                    using (var stream = new MemoryStream(fileBytes))
                    using (var reader = new StreamReader(stream))
                    {
                        records = parser.ParseStreamAndValidateFooter(reader, rootTraceParentId);
                    }

                    Assert.Single(records);
                    await repository.BulkInsertStagingAsync(records, fileReference, rootTraceParentId, CancellationToken.None);

                    using (var procConnection = new SqlConnection(_connectionString))
                    {
                        await procConnection.OpenAsync();
                        await procConnection.ExecuteAsync(
                            "ledger.sp_ProcessInboundClearing",
                            new { FileReference = fileReference },
                            commandType: CommandType.StoredProcedure);
                    }
                }

                using (var verifyConnection = new SqlConnection(_connectionString))
                {
                    await verifyConnection.OpenAsync();

                    var outboxEvent = await verifyConnection.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT TOP 1 EventId, EventType, Payload, TraceId 
                    FROM ledger.OutboxEvents 
                    WHERE EventType = 'OverpaymentDetectedEvent' AND Payload LIKE @ClientId 
                    ORDER BY CreatedAt DESC",
                        new { ClientId = $"%{clientId}%" });

                    Assert.NotNull(outboxEvent);

                    string dbTraceId = outboxEvent.TraceId;
                    string rawPayload = outboxEvent.Payload;

                    var eventData = JsonSerializer.Deserialize<OverpaymentDetectedEvent>(rawPayload)
                                    ?? throw new InvalidOperationException("Failed to deserialize outbox event.");

                    if (rootActivity != null)
                    {
                        rootActivity.SetTag("fintech.overpayment.success", true);
                        rootActivity.SetTag("fintech.client.id", eventData.ClientId);
                        rootActivity.SetTag("fintech.excess.amount", eventData.OverpaidAmount);
                        rootActivity.AddEvent(new ActivityEvent("OutboxVerificationCompleted"));
                    }

                    _output.WriteLine($"[W3C VERIFIED] Flow TraceId: {rootActivity?.TraceId} matches Database Event TraceId: {dbTraceId}");
                    _output.WriteLine($"[AUDIT OUTBOX] Excess Amount: {eventData.OverpaidAmount} EUR locked for Client {eventData.ClientId}.");

                    Assert.Equal(rootTraceParentId, dbTraceId);
                    Assert.Equal(clientId, eventData.ClientId);
                    Assert.Equal(fileReference, eventData.FileReference);
                    Assert.Equal(referenceNumber, eventData.ReferenceNumber);
                    Assert.Equal(expectedExcess, eventData.OverpaidAmount);
                }
            }
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = originalCulture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = originalUiCulture;
        }
    }

}
