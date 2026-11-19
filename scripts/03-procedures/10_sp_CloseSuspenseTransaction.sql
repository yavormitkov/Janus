USE JanusFinancialDb;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('ledger.sp_CloseSuspenseTransaction', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE ledger.sp_CloseSuspenseTransaction;
END
GO

CREATE PROCEDURE ledger.sp_CloseSuspenseTransaction
    @TransactionId UNIQUEIDENTIFIER,
    @UserId UNIQUEIDENTIFIER,
    @TargetStatus INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @TargetStatus NOT IN (40, 70)
    BEGIN
        RAISERROR('Invalid target status: Allowed values are 40 (Rejected) or 70 (ReturnToProvider).', 16, 1);
        RETURN;
    END;

    BEGIN TRANSACTION;

    BEGIN TRY
        DECLARE @CurrentStatus INT;
        DECLARE @MakerUserId UNIQUEIDENTIFIER;

        SELECT 
            @CurrentStatus = SuspenseStatus,
            @MakerUserId = MakerUserId
        FROM ledger.SuspenseTransactions WITH (ROWLOCK, XLOCK)
        WHERE TransactionId = @TransactionId;

        IF @CurrentStatus IS NULL
        BEGIN
            RAISERROR('Suspense record missing.', 16, 2);
            RETURN;
        END;

        IF @CurrentStatus NOT IN (20, 60, 80)
        BEGIN
            RAISERROR('Record is not in a closable state (PendingApproval or Duplicate).', 16, 3);
            RETURN;
        END;

        IF @MakerUserId IS NOT NULL AND @MakerUserId = @UserId
        BEGIN
            RAISERROR('Security violation: Maker and Checker cannot be identical.', 16, 4);
            RETURN;
        END;

        UPDATE ledger.SuspenseTransactions
        SET 
            SuspenseStatus = @TargetStatus,
            CheckerUserId = @UserId,
            UpdatedAt = SYSUTCDATETIME()
        WHERE TransactionId = @TransactionId;

        EXEC staging.sp_UpdateSuspenseInboundStatus
            @TransactionId = @TransactionId,
            @Status = 4;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;
        THROW;
    END CATCH
END;
GO
