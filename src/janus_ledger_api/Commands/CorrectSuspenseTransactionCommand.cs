using System;

namespace Janus.Ledger.Api.Commands;

public record CorrectSuspenseTransactionCommand(
    Guid TransactionId,
    string CorrectedEGN,
    string? CorrectedClientName,
    Guid MakerUserId
);
