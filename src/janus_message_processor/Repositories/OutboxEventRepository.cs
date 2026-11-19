using System;
using System.Data;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Dapper;

namespace JanusMessageProcessor.Repositories;

public sealed class OutboxEventRepository
{
    private readonly string _connectionString;

    public OutboxEventRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IEnumerable<(Guid EventId, string TraceId, string Payload)>> GetAndProcessPendingOutboxEventsAsync(CancellationToken cancellationToken)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var result = await connection.QueryAsync<(Guid EventId, string TraceId, string Payload)>(
            "ledger.sp_GetAndProcessPendingOutboxEvents",
            commandType: CommandType.StoredProcedure
        );
        return result.AsList();
    }

    public async Task UpdateEventStatusAsync(Guid eventId, int status, CancellationToken cancellationToken)
    {
        using var connection = new SqlConnection(_connectionString);

        if (connection is System.Data.Common.DbConnection dbConnection)
        {
            await dbConnection.OpenAsync(cancellationToken);
        }
        else
        {
            connection.Open();
        }

        var parameters = new DynamicParameters();
        parameters.Add("@EventId", eventId, DbType.Guid);
        parameters.Add("@Status", status, DbType.Int32);

        await connection.ExecuteAsync(new CommandDefinition(
            "ledger.sp_UpdateOutboxEventStatus",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken
        ));
    }

}
