using System;

namespace Janus.Core.Shared.Events;

public sealed record OverpaymentDetectedEvent(
    Guid ClientId,
    decimal OriginalAmount,
    decimal OverpaidAmount,
    string ReferenceNumber,
    string FileReference,
    string TraceId
);
