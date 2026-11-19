namespace Janus.Ledger.Api.Infrastructure;

using System.Data;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
