namespace Janus.Ledger.Api.Commands;

public record ApproveSuspenseTransactionCommand(
    Guid TransactionId,
    Guid CheckerUserId
);
