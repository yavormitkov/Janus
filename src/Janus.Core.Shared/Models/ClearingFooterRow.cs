namespace Janus.Core.Shared.Models;

public sealed record ClearingFooterRow(
    int TotalRecordsCount,
    decimal TotalFileAmount,
    int RowIndex);
