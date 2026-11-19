USE JanusFinancialDb;
GO
delete from ledger.debts;
DECLARE @IvanClientId UNIQUEIDENTIFIER = '2CF4A7D5-8AD0-4F61-A6EF-12EB2D2BD8C5';
DECLARE @MariaClientId UNIQUEIDENTIFIER = 'ED612846-6EC8-4EFC-BF08-2E68C9A12DB1';

INSERT INTO ledger.Clients (ClientId, EGN, ClientName)
VALUES 
(@IvanClientId, N'8405121234', N'Ivan Ivanov'),
(@MariaClientId, N'7911055678', N'Maria Petrova');

INSERT INTO ledger.Debts (DebtId, ClientId, InterestDebt, ExpensesDebt, PrincipalDebt, TotalDebt, Currency)
VALUES 
(NEWID(), @IvanClientId, 150.00, 50.00, 4000.00, 4200.00, 'EUR'),
(NEWID(), @MariaClientId, 400.00, 100.00, 11500.00, 12000.00, 'EUR');
GO

