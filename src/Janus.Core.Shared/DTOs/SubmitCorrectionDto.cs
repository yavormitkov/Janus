namespace Janus.Core.Shared.DTOs;

public record SubmitCorrectionDto(
    Guid TransactionId,
    string CorrectedEGN
);
