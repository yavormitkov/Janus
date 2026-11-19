namespace Janus.Ledger.Api.Repositories;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapper;
using Janus.Core.Shared.Enums;
using Janus.Ledger.Api.Infrastructure;
using Janus.Ledger.Api.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

public class DapperSuspenseTransactionRepository : IDapperSuspenseTransactionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DapperSuspenseTransactionRepository> _logger;
    private static readonly ActivitySource LedgerSource = new("Janus.Ledger.Core", "1.0.0");

    public DapperSuspenseTransactionRepository(IDbConnectionFactory connectionFactory, ILogger<DapperSuspenseTransactionRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<SuspenseTransaction?> GetByIdAsync(Guid transactionId)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT 
                TransactionId, TraceId, FileReference, InboundEGN, InboundClientName, 
                CorrectedEGN, CorrectedClientName, Amount, ReferenceNumber, 
                SuspenseStatus, MakerUserId, CheckerUserId, CreatedAt, UpdatedAt 
            FROM ledger.SuspenseTransactions 
            WHERE TransactionId = @TransactionId";

        return await connection.QuerySingleOrDefaultAsync<SuspenseTransaction>(
            sql,
            new { TransactionId = transactionId }
        );
    }

    public async Task<IEnumerable<SuspenseTransaction>> GetUnresolvedAsync(SuspenseStatus status)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT 
                tx.TransactionId, tx.TraceId, tx.FileReference, tx.InboundEGN, tx.InboundClientName, 
                tx.CorrectedEGN, tx.CorrectedClientName, tx.Amount, tx.ReferenceNumber, 
                tx.SuspenseStatus, tx.MakerUserId, tx.CheckerUserId, tx.CreatedAt, tx.UpdatedAt,
                stg.ReferenceNumber AS OriginalReferenceNumber,
                stg.FileReference AS OriginalFileName,
                stg.RawEGN AS OriginalEGN,
                stg.RawClientName AS OriginalClientName,
                stg.CreatedAt AS OriginalCreatedAt, 
                stg.Amount as OriginalAmount
            FROM ledger.SuspenseTransactions tx
            LEFT JOIN staging.InboundTransactions stg
                ON stg.ReferenceNumber = tx.ReferenceNumber
                AND stg.TransactionId <> tx.TransactionId
            WHERE tx.SuspenseStatus = @StatusUnresolved";

        return await connection.QueryAsync<SuspenseTransaction>(
            sql,
            new { StatusUnresolved = (int)status }
        );
    }


    public async Task<bool> UpdateToPendingApprovalAsync(
        Guid transactionId,
        Guid makerUserId,
        string correctedEgn,
        string? correctedName)
    {
        using var connection = _connectionFactory.CreateConnection();

        var parameters = new DynamicParameters();
        parameters.Add("@TransactionId", transactionId, DbType.Guid);
        parameters.Add("@MakerUserId", makerUserId, DbType.Guid);
        parameters.Add("@CorrectedEgn", correctedEgn, DbType.String);
        parameters.Add("@CorrectedClientName", correctedName, DbType.String);

        try
        {
            await connection.ExecuteAsync(
                "ledger.sp_ProposeSuspenseCorrection",
                parameters,
                commandType: CommandType.StoredProcedure
            );
            return true;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Error occurred while proposing suspense correction for transaction {TransactionId}", transactionId);
            return false;
        }
    }

    public async Task<bool> ApproveAsync(Guid transactionId, Guid checkerUserId)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var activity = await StartLinkedActivityAsync(connection, transactionId, "DapperSuspenseTransactionRepository.Approve");

        activity?.SetTag("suspense.transaction_id", transactionId);
        activity?.SetTag("checker.user_id", checkerUserId);

        var parameters = new DynamicParameters();
        parameters.Add("@TransactionId", transactionId, DbType.Guid);
        parameters.Add("@CheckerUserId", checkerUserId, DbType.Guid);

        try
        {
            await connection.ExecuteAsync(
                "ledger.sp_ApplySuspenseCorrection",
                parameters,
                commandType: CommandType.StoredProcedure
            );

            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (SqlException ex) when (ex.Number == 50000 && ex.Message.Contains("Maker and Checker cannot be identical"))
        {
            RecordFourEyesViolation(activity, ex, transactionId, checkerUserId, "Approve");
            throw new UnauthorizedAccessException("Security violation: Identity Constraint breached at the storage tier.", ex);
        }
        catch (SqlException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error occurred while approving suspense correction for transaction {TransactionId}", transactionId);
            return false;
        }
    }

    public async Task<bool> RejectAsync(Guid transactionId, Guid checkerUserId)
    {
        return await CloseSuspenseAsync(transactionId, checkerUserId, SuspenseStatus.Rejected);
    }

    public async Task<bool> ReturnToProviderAsync(Guid transactionId, Guid operatorUserId)
    {
        return await CloseSuspenseAsync(transactionId, operatorUserId, SuspenseStatus.ReturnToProvider);
    }

    private async Task<bool> CloseSuspenseAsync(Guid transactionId, Guid userId, SuspenseStatus targetStatus)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var activity = await StartLinkedActivityAsync(connection, transactionId, $"DapperSuspenseTransactionRepository.{targetStatus}");

        activity?.SetTag("suspense.transaction_id", transactionId);
        activity?.SetTag("suspense.target_status", targetStatus.ToString());
        activity?.SetTag("operator.user_id", userId);

        var parameters = new DynamicParameters();
        parameters.Add("@TransactionId", transactionId, DbType.Guid);
        parameters.Add("@UserId", userId, DbType.Guid);
        parameters.Add("@TargetStatus", (int)targetStatus, DbType.Int32);

        try
        {
            await connection.ExecuteAsync(
                "ledger.sp_CloseSuspenseTransaction",
                parameters,
                commandType: CommandType.StoredProcedure
            );

            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch (SqlException ex) when (ex.Number == 50000 && ex.Message.Contains("Maker and Checker cannot be identical"))
        {
            RecordFourEyesViolation(activity, ex, transactionId, userId, targetStatus.ToString());
            throw new UnauthorizedAccessException("Security violation: Identity Constraint breached at the storage tier.", ex);
        }
        catch (SqlException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error closing suspense transaction {TransactionId} with status {Status}", transactionId, targetStatus);
            return false;
        }
    }

    private async Task<Activity?> StartLinkedActivityAsync(IDbConnection connection, Guid transactionId, string operationName)
    {
        if (connection is DbConnection dbConnection && dbConnection.State != ConnectionState.Open)
        {
            await dbConnection.OpenAsync();
        }
        else if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        const string traceSql = "SELECT TraceId FROM ledger.SuspenseTransactions WHERE TransactionId = @TransactionId";
        var savedTraceParent = await connection.QuerySingleOrDefaultAsync<string>(traceSql, new { TransactionId = transactionId });

        if (!string.IsNullOrEmpty(savedTraceParent) && ActivityContext.TryParse(savedTraceParent, null, out var parentContext))
        {
            return LedgerSource.StartActivity(operationName, ActivityKind.Internal, parentContext);
        }

        return null;
    }

    private void RecordFourEyesViolation(Activity? activity, SqlException ex, Guid transactionId, Guid userId, string operation)
    {
        if (activity != null)
        {
            activity.SetTag("security.compromise_attempted", true);
            activity.SetTag("security.fraud_alert", "CRITICAL_HIGH");
            activity.SetTag("security.violating_operator_id", userId);

            var eventTags = new ActivityTagsCollection
            {
                { "exception.message", ex.Message },
                { "sql.error.number", ex.Number }
            };
            activity.AddEvent(new ActivityEvent("FourEyesViolationTrapped", DateTime.UtcNow, eventTags));
        }

        _logger.LogCritical(ex, "SECURITY VIOLATION: Same Maker and Checker attempt trapped during {Operation} for transaction {TransactionId} by user {UserId}", operation, transactionId, userId);
    }
}
