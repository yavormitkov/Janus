USE JanusFinancialDb;
GO

IF TYPE_ID('ledger.PaymentAllocationBatchType') IS NOT NULL
BEGIN
    DROP TYPE ledger.PaymentAllocationBatchType;
END
GO

CREATE TYPE ledger.PaymentAllocationBatchType AS TABLE
(
    RowId INT IDENTITY(1,1) PRIMARY KEY,
    TransactionId UNIQUEIDENTIFIER NOT NULL,
    TraceId NVARCHAR(55) NOT NULL,
    EGN NVARCHAR(50) NOT NULL,
    Amount DECIMAL(18, 2) NOT NULL
);
GO

IF OBJECT_ID('ledger.sp_ProcessInboundClearing', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE ledger.sp_ProcessInboundClearing;
END
GO

CREATE PROCEDURE ledger.sp_ProcessInboundClearing
    @FileReference NVARCHAR(150)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF EXISTS (SELECT 1 FROM staging.ProcessedFiles WHERE FileName = @FileReference AND IsCleared = 1)
    BEGIN
        RETURN;
    END;
	BEGIN TRY
		BEGIN TRANSACTION;

        INSERT INTO ledger.SuspenseTransactions 
        (
            TransactionId, TraceId, FileReference, InboundEGN, 
            InboundClientName, Amount, ReferenceNumber, SuspenseStatus
        )
        SELECT 
            stg.TransactionId, stg.TraceId, stg.FileReference, stg.RawEGN,
            stg.RawClientName, stg.Amount, stg.ReferenceNumber,
            CASE 
                WHEN r.ReferenceNumber IS NOT NULL THEN 60
                WHEN tx.TransactionId IS NOT NULL THEN 80
                ELSE 10
            END
        FROM staging.InboundTransactions stg
        LEFT JOIN ledger.Clients cl WITH (NOLOCK) ON cl.EGN = stg.RawEGN
        LEFT JOIN ledger.IdempotencyRegistry r ON stg.ReferenceNumber = r.ReferenceNumber 
        LEFT JOIN ledger.SuspenseTransactions tx ON stg.ReferenceNumber = tx.ReferenceNumber
        LEFT JOIN ledger.SuspenseTransactions existing_pk ON stg.TransactionId = existing_pk.TransactionId
        WHERE stg.FileReference = @FileReference
          AND stg.Processed = 0
          AND existing_pk.TransactionId IS NULL
          AND (cl.ClientId IS NULL OR r.ReferenceNumber IS NOT NULL OR tx.TransactionId IS NOT NULL);


        UPDATE stg
        SET stg.Processed = 3
        FROM staging.InboundTransactions stg
        INNER JOIN ledger.SuspenseTransactions tx ON stg.TransactionId = tx.TransactionId
        WHERE stg.FileReference = @FileReference
          AND stg.Processed = 0;

        DECLARE @ValidTransactions ledger.PaymentAllocationBatchType;

        INSERT INTO @ValidTransactions (TransactionId, TraceId, EGN, Amount)
        SELECT 
            stg.TransactionId, stg.TraceId, stg.RawEGN, stg.Amount
        FROM staging.InboundTransactions stg
        INNER JOIN ledger.Clients cl WITH (NOLOCK) ON cl.EGN = stg.RawEGN
        LEFT JOIN ledger.IdempotencyRegistry r ON stg.ReferenceNumber = r.ReferenceNumber 
        WHERE stg.FileReference = @FileReference
          AND stg.Processed = 0
          AND r.ReferenceNumber IS NULL
        ORDER BY stg.RowIndex ASC;


        INSERT INTO ledger.IdempotencyRegistry (ReferenceNumber, FileName, EGN)
        SELECT stg.ReferenceNumber, @FileReference, vt.EGN
        FROM @ValidTransactions vt
        INNER JOIN staging.InboundTransactions stg ON vt.TransactionId = stg.TransactionId;

        EXEC ledger.sp_AllocatePaymentBatch @Batch = @ValidTransactions;

        UPDATE stg
        SET stg.Processed = 1
        FROM staging.InboundTransactions stg
        INNER JOIN @ValidTransactions vt ON stg.TransactionId = vt.TransactionId
        WHERE stg.FileReference = @FileReference;

        UPDATE staging.ProcessedFiles 
        SET IsCleared = 1 
        WHERE FileName = @FileReference;

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
