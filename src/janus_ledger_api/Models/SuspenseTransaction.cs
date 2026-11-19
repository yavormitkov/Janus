namespace Janus.Ledger.Api.Models;

public class SuspenseTransaction
{
    public Guid TransactionId { get; set; }
    public string FileReference { get; set; } = string.Empty;
    public string InboundEGN { get; set; } = string.Empty;
    public string InboundClientName { get; set; } = string.Empty;
    public string? CorrectedEGN { get; set; }
    public string? CorrectedClientName { get; set; }
    public decimal Amount { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public int SuspenseStatus { get; set; }
    public Guid? MakerUserId { get; set; }
    public Guid? CheckerUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? OriginalReferenceNumber { get; set; }
    public string? OriginalFileName { get; set; }
    public string? OriginalEGN { get; set; }
    public string? OriginalClientName { get; set; }
    public decimal? OriginalAmount { get; set; }
    public DateTime? OriginalCreatedAt { get; set; }
}
