USE JanusFinancialDb;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('staging.sp_UpdateSuspenseInboundStatus', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE staging.sp_UpdateSuspenseInboundStatus;
END
GO

CREATE PROCEDURE staging.sp_UpdateSuspenseInboundStatus
    @TransactionId UNIQUEIDENTIFIER,
    @Status INT = 1
AS
BEGIN
    SET NOCOUNT ON;

    IF @Status NOT IN (1, 2, 3, 4)
    BEGIN
        RAISERROR('Invalid target status: Allowed values are 2 (ManuallyProcessed) or 4 (Error).', 16, 1);
        RETURN;
    END;

    UPDATE staging.InboundTransactions
    SET 
        Processed = @Status
    WHERE TransactionId = @TransactionId;

    IF @@ROWCOUNT = 0
    BEGIN
        RAISERROR('Reconciliation failure: Staging transaction not found.', 16, 1);
    END;
END;
GO
