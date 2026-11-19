namespace Janus.Ledger.Api.Repositories;

using Janus.Core.Shared.Enums;
using Janus.Ledger.Api.Models;

public interface IDapperSuspenseTransactionRepository
{
    Task<SuspenseTransaction?> GetByIdAsync(Guid transactionId);
    Task<IEnumerable<SuspenseTransaction>> GetUnresolvedAsync(SuspenseStatus status);
    Task<bool> UpdateToPendingApprovalAsync(Guid transactionId, Guid makerUserId, string correctedEgn, string? correctedName);
    Task<bool> ApproveAsync(Guid transactionId, Guid checkerUserId);
    Task<bool> RejectAsync(Guid transactionId, Guid checkerUserId);
    Task<bool> ReturnToProviderAsync(Guid transactionId, Guid operatorUserId);
}
