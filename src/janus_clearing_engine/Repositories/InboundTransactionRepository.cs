namespace JanusClearingEngine.Repositories;

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Janus.Core.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Microsoft.Extensions.Configuration;

public sealed class InboundTransactionRepository
{
    private readonly string _connectionString;

    private static readonly SqlMetaData[] StagingSchema =
    [
        new SqlMetaData("TransactionId", SqlDbType.UniqueIdentifier),
        new SqlMetaData("RawEGN", SqlDbType.NVarChar, 50),
        new SqlMetaData("RawClientName", SqlDbType.NVarChar, 250),
        new SqlMetaData("Amount", SqlDbType.Decimal, 18, 4),
        new SqlMetaData("ReferenceNumber", SqlDbType.NVarChar, 100),
        new SqlMetaData("RowIndex", SqlDbType.Int),
        new SqlMetaData("TraceId", SqlDbType.Char, 55),
        new SqlMetaData("Processed", SqlDbType.Int)
    ];

    public InboundTransactionRepository(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("JanusFinancialDb")
            ?? throw new InvalidOperationException("Missing connection string.");
        _connectionString = Environment.ExpandEnvironmentVariables(raw);
    }

    public async Task BulkInsertStagingAsync(IReadOnlyList<ClearingDataRow> records, string fileReference, string w3cTraceId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var parameters = new
        {
            Records = StreamSqlRecords(records, w3cTraceId).AsTableValuedParameter("staging.InboundTransactionType"),
            FileReference = fileReference
        };

        var command = new CommandDefinition(
            "staging.sp_InsertInboundTransaction",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken);

        await connection.ExecuteAsync(command);
    }

    private static IEnumerable<SqlDataRecord> StreamSqlRecords(IReadOnlyList<ClearingDataRow> records, string w3cTraceId)
    {
        var sqlRecord = new SqlDataRecord(StagingSchema);

        foreach (var r in records)
        {
            sqlRecord.SetGuid(0, Guid.NewGuid());
            sqlRecord.SetString(1, r.RawEgn);
            sqlRecord.SetString(2, r.RawClientName ?? string.Empty);
            sqlRecord.SetDecimal(3, r.Amount);
            sqlRecord.SetString(4, r.ReferenceNumber);
            sqlRecord.SetInt32(5, r.RowIndex);
            sqlRecord.SetString(6, w3cTraceId);
            sqlRecord.SetInt32(7, 0);

            yield return sqlRecord;
        }
    }
}
