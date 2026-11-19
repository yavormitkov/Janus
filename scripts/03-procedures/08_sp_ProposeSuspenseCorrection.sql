USE JanusFinancialDb;
GO

CREATE PROCEDURE ledger.sp_ProposeSuspenseCorrection
    @TransactionId UNIQUEIDENTIFIER,
    @MakerUserId UNIQUEIDENTIFIER,
    @CorrectedEgn VARCHAR(10),
    @CorrectedClientName NVARCHAR(255)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM ledger.Clients WHERE EGN = @CorrectedEgn)
    BEGIN
        RAISERROR('Validation failed: The provided EGN does not exist in the clients registry.', 16, 1);
        RETURN;
    END;

    UPDATE ledger.SuspenseTransactions 
    SET SuspenseStatus = 20, 
        MakerUserId = @MakerUserId, 
        CorrectedEGN = @CorrectedEgn, 
        CorrectedClientName = @CorrectedClientName, 
        UpdatedAt = SYSUTCDATETIME() 
    WHERE TransactionId = @TransactionId 
      AND SuspenseStatus = 10;

    IF @@ROWCOUNT = 0
    BEGIN
        RAISERROR('Proposal injection failed: Suspense transaction record missing or not in a modifiable status.', 16, 1);
    END;
END;
GO
