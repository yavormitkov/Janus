using System;
using System.Data;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Dapper;

using Janus.Core.Shared.DTOs;

namespace JanusRawDataProcessor.Repositories;

public sealed class ClearingProcessRepository
{
    private readonly string _connectionString;

    public ClearingProcessRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IEnumerable<ProcessedFileDto>> GetPendingFilesAsync()
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string query = @"
        SELECT DISTINCT FileId, TraceId, FileName, ProcessedAt 
        FROM staging.ProcessedFiles
        WHERE IsCleared = 0;";

        return await connection.QueryAsync<ProcessedFileDto>(query);
    }

    public async Task ProcessInboundClearingAsync(string fileReference)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            "ledger.sp_ProcessInboundClearing",
            new { FileReference = fileReference },
            commandType: CommandType.StoredProcedure,
            commandTimeout: 300
        );
    }
}
