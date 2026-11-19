USE JanusFinancialDb
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE TYPE staging.InboundTransactionType AS TABLE
(
    TransactionId UNIQUEIDENTIFIER,
    RawEGN NVARCHAR(50),
    RawClientName NVARCHAR(250),
    Amount DECIMAL(18, 4),
    ReferenceNumber NVARCHAR(100),
    RowIndex INT,
	TraceId CHAR(55) NOT NULL,
    Processed INT
);
GO

CREATE PROCEDURE staging.sp_InsertInboundTransaction
    @Records staging.InboundTransactionType READONLY,
    @FileReference NVARCHAR(250)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    BEGIN TRY
        INSERT INTO staging.InboundTransactions 
            (TransactionId, FileReference, RawEGN, RawClientName, Amount, ReferenceNumber, RowIndex, TraceId, Processed)
        SELECT 
            TransactionId, @FileReference, RawEGN, RawClientName, Amount, ReferenceNumber, RowIndex, TraceId, Processed
        FROM @Records;

        INSERT INTO staging.ProcessedFiles (FileName, TraceId, IsCleared) 
        VALUES (@FileReference, (SELECT MIN(TraceId) FROM @Records), 0);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
            
        THROW;
    END CATCH
END;
GO
