namespace Janus.Ledger.Api.Models;

using System;

public class SuspenseTransactionHistory
{
    public long Id { get; set; }
    public string ModifierUserId { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
}
