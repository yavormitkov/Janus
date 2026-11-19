using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Janus.Ledger.Api.Infrastructure;

public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("JanusFinancialDb")
            ?? throw new InvalidOperationException("Connection string 'JanusFinancialDb' not found.");

        _connectionString = Environment.ExpandEnvironmentVariables(raw);
    }

    public IDbConnection CreateConnection()
    {
        return new SqlConnection(_connectionString);
    }
}
