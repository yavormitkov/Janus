USE JanusFinancialDb;
GO

IF OBJECT_ID('ledger.sp_AllocatePaymentBatch', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE ledger.sp_AllocatePaymentBatch;
END
GO

CREATE PROCEDURE ledger.sp_AllocatePaymentBatch
    @Batch ledger.PaymentAllocationBatchType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM @Batch)
    BEGIN
        RETURN;
    END;

    IF EXISTS (
        SELECT 1 
        FROM @Batch b
        LEFT JOIN ledger.Clients cl WITH (NOLOCK) ON b.EGN = cl.EGN
        LEFT JOIN ledger.Debts d ON cl.ClientId = d.ClientId
        WHERE cl.ClientId IS NULL OR d.DebtId IS NULL
    )
    BEGIN
        RAISERROR('No active debt or client found for one or more provided EGNs.', 16, 1);
        RETURN;
    END;

    DECLARE @CalculatedRows TABLE
    (
        TransactionId UNIQUEIDENTIFIER,
        ClientId UNIQUEIDENTIFIER,
        TraceId NVARCHAR(55),
        OriginalAmount DECIMAL(18, 2),
        RowOverpayment DECIMAL(18, 2),
        NewInterest DECIMAL(18, 2),
        NewExpenses DECIMAL(18, 2),
        NewPrincipal DECIMAL(18, 2),
        IsFinalRowForClient BIT
    );

    WITH CumulativePayments AS
    (
        SELECT 
            b.RowId,
            b.TransactionId,
            b.TraceId,
            b.Amount AS CurrentAmount,
            cl.ClientId,
            d.InterestDebt,
            d.ExpensesDebt,
            d.PrincipalDebt,
            SUM(b.Amount) OVER (PARTITION BY cl.ClientId ORDER BY b.RowId ROWS UNBOUNDED PRECEDING) AS CumulPaidAfter,
            SUM(b.Amount) OVER (PARTITION BY cl.ClientId ORDER BY b.RowId ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS CumulPaidBefore,
            ROW_NUMBER() OVER (PARTITION BY cl.ClientId ORDER BY b.RowId DESC) AS ReverseRowNum
        FROM @Batch b
        INNER JOIN ledger.Clients cl WITH (NOLOCK) ON b.EGN = cl.EGN
        INNER JOIN ledger.Debts d WITH (UPDLOCK, ROWLOCK, HOLDLOCK) ON cl.ClientId = d.ClientId
    )
    INSERT INTO @CalculatedRows
    (
        TransactionId, ClientId, TraceId, OriginalAmount, RowOverpayment,
        NewInterest, NewExpenses, NewPrincipal, IsFinalRowForClient
    )
    SELECT 
        cp.TransactionId,
        cp.ClientId,
        cp.TraceId,
        cp.CurrentAmount,
        wfAfter.Overpayment - ISNULL(wfBefore.Overpayment, 0.00),
        wfAfter.NewInterestDebt,
        wfAfter.NewExpensesDebt,
        wfAfter.NewPrincipalDebt,
        CASE WHEN cp.ReverseRowNum = 1 THEN 1 ELSE 0 END
    FROM CumulativePayments cp
    CROSS APPLY ledger.fn_CalculateWaterfall(cp.InterestDebt, cp.ExpensesDebt, cp.PrincipalDebt, cp.CumulPaidAfter) wfAfter
    OUTER APPLY ledger.fn_CalculateWaterfall(cp.InterestDebt, cp.ExpensesDebt, cp.PrincipalDebt, ISNULL(cp.CumulPaidBefore, 0.00)) wfBefore;

    UPDATE d
    SET 
        d.InterestDebt = cr.NewInterest,
        d.ExpensesDebt = cr.NewExpenses,
        d.PrincipalDebt = cr.NewPrincipal,
        d.TotalDebt = cr.NewInterest + cr.NewExpenses + cr.NewPrincipal,
        d.IsArchived = CASE WHEN (cr.NewInterest + cr.NewExpenses + cr.NewPrincipal) = 0.00 THEN 1 ELSE 0 END,
        d.UpdatedAt = SYSUTCDATETIME()
    FROM ledger.Debts d
    INNER JOIN @CalculatedRows cr ON d.ClientId = cr.ClientId
    WHERE cr.IsFinalRowForClient = 1;

    INSERT INTO ledger.Overpayments (OverpaymentId, TransactionId, ClientId, Amount)
    SELECT 
        NEWID(),
        cr.TransactionId,
        cr.ClientId,
        cr.RowOverpayment
    FROM @CalculatedRows cr
    WHERE cr.RowOverpayment > 0;

    INSERT INTO ledger.OutboxEvents (EventId, EventType, Payload, TraceId, IsProcessed, CreatedAt)
    SELECT 
        NEWID(),
        'OverpaymentDetectedEvent',
        (
            SELECT 
                cr.ClientId AS ClientId,
                cr.OriginalAmount AS OriginalAmount,
                cr.RowOverpayment AS OverpaidAmount,
                it.ReferenceNumber AS ReferenceNumber,
                it.FileReference AS FileReference
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        ),
        cr.TraceId,
        0,
        SYSUTCDATETIME()
    FROM @CalculatedRows cr
    INNER JOIN staging.InboundTransactions it ON cr.TransactionId = it.TransactionId
    WHERE cr.RowOverpayment > 0;
END;
GO
