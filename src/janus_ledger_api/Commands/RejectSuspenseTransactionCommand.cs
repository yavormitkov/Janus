namespace Janus.Ledger.Api.Commands;

public record RejectSuspenseTransactionCommand(
    Guid TransactionId,
    Guid CheckerUserId
);
