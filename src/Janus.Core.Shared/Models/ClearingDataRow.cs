namespace Janus.Core.Shared.Models;

public sealed record ClearingDataRow(
    string RawEgn,
    decimal Amount,
    string ReferenceNumber,
    string? RawClientName,
    int RowIndex,
    string TraceId);
