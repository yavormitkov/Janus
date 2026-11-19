using System;
using Janus.Core.Shared.Enums;

namespace Janus.Core.Shared.DTOs;

public record SuspenseTransactionItemDto(
    Guid TransactionId,
    string FileReference,
    string InboundEGN,
    string InboundClientName,
    string? CorrectedEGN,
    decimal Amount,
    string ReferenceNumber,
    SuspenseStatus SuspenseStatus,
    bool IsNameMismatch,
    Guid? MakerUserId,
    Guid? CheckerUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? OriginalReferenceNumber = null,
    string? OriginalFileName = null,
    string? OriginalEGN = null,
    string? OriginalClientName = null,
    decimal? OriginalAmount = null,
    DateTime? OriginalCreatedAt = null
);
