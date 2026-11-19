USE JanusFinancialDb;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('ledger.sp_ApplySuspenseCorrection', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE ledger.sp_ApplySuspenseCorrection;
END
GO

CREATE PROCEDURE ledger.sp_ApplySuspenseCorrection
    @TransactionId UNIQUEIDENTIFIER,
    @CheckerUserId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    BEGIN TRY
        DECLARE @TraceId CHAR(55);
        DECLARE @CurrentStatus INT;
        DECLARE @MakerUserId UNIQUEIDENTIFIER;
        DECLARE @CorrectedEgn VARCHAR(10);
        DECLARE @InboundEgn VARCHAR(10);
        DECLARE @Amount DECIMAL(18, 2);
        DECLARE @FileReference NVARCHAR(150);

        SELECT 
            @TraceId = TraceId,
            @CurrentStatus = SuspenseStatus,
            @MakerUserId = MakerUserId,
            @CorrectedEgn = CorrectedEGN,
            @InboundEgn = InboundEGN,
            @Amount = Amount,
            @FileReference = FileReference
        FROM ledger.SuspenseTransactions WITH (ROWLOCK, XLOCK)
        WHERE TransactionId = @TransactionId;

        IF @CurrentStatus IS NULL
        BEGIN
            RAISERROR('Suspense record missing.', 16, 1);
            RETURN;
        END;

        IF @CurrentStatus <> 20
        BEGIN
            RAISERROR('Record is not in pending approval state.', 16, 2);
            RETURN;
        END;

        IF @MakerUserId = @CheckerUserId
        BEGIN
            RAISERROR('Security violation: Maker and Checker cannot be identical.', 16, 3);
            RETURN;
        END;

        IF @CorrectedEgn IS NULL
        BEGIN
            RAISERROR('Target identity modifier is missing.', 16, 4);
            RETURN;
        END;

        UPDATE ledger.SuspenseTransactions
        SET 
            SuspenseStatus = 30,
            CheckerUserId = @CheckerUserId,
            UpdatedAt = SYSUTCDATETIME()
        WHERE TransactionId = @TransactionId;

        EXEC ledger.sp_AllocatePayment 
            @EGN = @CorrectedEgn,
            @Amount = @Amount,
            @TransactionId = @TransactionId,
            @TraceId = @TraceId;

        EXEC staging.sp_UpdateSuspenseInboundStatus
            @TransactionId = @TransactionId,
			@Status = 2;

        INSERT INTO audit.MakerCheckerHistory (TransactionId, TraceId, OriginalEGN, CorrectedEGN, ProposedBy, ApprovedBy) 
        VALUES (@TransactionId, @TraceId, @InboundEgn, @CorrectedEgn, @MakerUserId, @CheckerUserId);

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
