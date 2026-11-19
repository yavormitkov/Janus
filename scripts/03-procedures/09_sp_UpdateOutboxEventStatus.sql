USE [JanusFinancialDb]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('ledger.sp_UpdateOutboxEventStatus', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE ledger.sp_UpdateOutboxEventStatus;
END
GO

CREATE PROCEDURE ledger.sp_UpdateOutboxEventStatus
    @EventId UNIQUEIDENTIFIER,
    @Status INT,
    @TraceId VARCHAR(55) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE ledger.OutboxEvents
    SET 
        IsProcessed = @Status,
        ProcessedAt = SYSUTCDATETIME()
    WHERE EventId = @EventId;
END;
GO