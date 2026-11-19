USE JanusFinancialDb;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID('ledger.fn_CalculateWaterfall', 'IF') IS NOT NULL
BEGIN
    DROP FUNCTION ledger.fn_CalculateWaterfall;
END
GO

CREATE FUNCTION ledger.fn_CalculateWaterfall
(
    @InterestDebt DECIMAL(18, 2),
    @ExpensesDebt DECIMAL(18, 2),
    @PrincipalDebt DECIMAL(18, 2),
    @PaymentAmount DECIMAL(18, 2)
)
RETURNS TABLE
AS
RETURN
(
    SELECT
        NewInterestDebt = CASE 
            WHEN @PaymentAmount >= @InterestDebt THEN 0.00 
            ELSE @InterestDebt - @PaymentAmount 
        END,
        NewExpensesDebt = CASE 
            WHEN RemAfterInterest >= @ExpensesDebt THEN 0.00 
            ELSE @ExpensesDebt - RemAfterInterest 
        END,
        NewPrincipalDebt = CASE 
            WHEN RemAfterExpenses >= @PrincipalDebt THEN 0.00 
            ELSE @PrincipalDebt - RemAfterExpenses 
        END,
        Overpayment = CASE 
            WHEN RemAfterExpenses > @PrincipalDebt THEN RemAfterExpenses - @PrincipalDebt 
            ELSE 0.00 
        END
    FROM (SELECT CASE WHEN @PaymentAmount > @InterestDebt THEN @PaymentAmount - @InterestDebt ELSE 0.00 END AS RemAfterInterest) step1
    CROSS APPLY (SELECT CASE WHEN RemAfterInterest > @ExpensesDebt THEN RemAfterInterest - @ExpensesDebt ELSE 0.00 END AS RemAfterExpenses) step2
);
GO
