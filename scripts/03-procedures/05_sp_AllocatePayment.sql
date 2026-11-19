USE JanusFinancialDb;
GO


IF OBJECT_ID('ledger.sp_AllocatePayment', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE ledger.sp_AllocatePayment;
END
GO

CREATE PROCEDURE ledger.sp_AllocatePayment
    @EGN NVARCHAR(50),
    @Amount DECIMAL(18, 2),
    @TransactionId UNIQUEIDENTIFIER,
    @TraceId NVARCHAR(55)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @SingleBatch ledger.PaymentAllocationBatchType;
    INSERT INTO @SingleBatch (TransactionId, TraceId, EGN, Amount)
    VALUES (@TransactionId, @TraceId, @EGN, @Amount);

    EXEC ledger.sp_AllocatePaymentBatch @Batch = @SingleBatch;
END;
GO
