USE [JanusFinancialDb]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('ledger.sp_GetAndProcessPendingOutboxEvents', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE ledger.sp_GetAndProcessPendingOutboxEvents;
END
GO

CREATE PROCEDURE ledger.sp_GetAndProcessPendingOutboxEvents
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        EventId,
        TraceId,
        Payload
    FROM ledger.OutboxEvents WITH (UPDLOCK, ROWLOCK, READPAST)
    WHERE IsProcessed = 0
    ORDER BY CreatedAt ASC;
END;
GO
