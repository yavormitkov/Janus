USE JanusFinancialDb;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO


CREATE TABLE ledger.IdempotencyRegistry
(
    RegistryId INT IDENTITY(1,1) NOT NULL,
    ReferenceNumber NVARCHAR(50) NOT NULL,
    FileName NVARCHAR(150) NOT NULL,
    EGN NVARCHAR(10) NOT NULL,
    RegisteredAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_IdempotencyRegistry PRIMARY KEY NONCLUSTERED (RegistryId),
    CONSTRAINT UQ_IdempotencyRegistry_ReferenceNumber UNIQUE CLUSTERED (ReferenceNumber)
);
GO



CREATE TABLE ledger.Clients
(
    ClientId UNIQUEIDENTIFIER NOT NULL,
    EGN NVARCHAR(10) NOT NULL,
    ClientName NVARCHAR(255) NOT NULL,
    CONSTRAINT PK_Clients PRIMARY KEY CLUSTERED (ClientId),
    CONSTRAINT UQ_Clients_EGN UNIQUE (EGN)
)
WITH 
(
    SYSTEM_VERSIONING = ON (HISTORY_TABLE = ledger.ClientsHistory),
    LEDGER = ON (APPEND_ONLY = OFF)
);

CREATE TABLE ledger.Debts
(
    DebtId UNIQUEIDENTIFIER NOT NULL,
    ClientId UNIQUEIDENTIFIER NOT NULL,
    Currency CHAR(3) NOT NULL, -- Добавена колона за валута (съответства на 'EUR' от инсърта)
    InterestDebt DECIMAL(18, 2) NOT NULL DEFAULT 0.00,
    ExpensesDebt DECIMAL(18, 2) NOT NULL DEFAULT 0.00,
    PrincipalDebt DECIMAL(18, 2) NOT NULL DEFAULT 0.00,
    TotalDebt DECIMAL(18, 2) NOT NULL DEFAULT 0.00,
    IsArchived BIT NOT NULL DEFAULT 0,
    UpdatedAt DATETIME2 NULL,
    SysStartTime DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEndTime DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStartTime, SysEndTime),
    CONSTRAINT PK_Debts PRIMARY KEY CLUSTERED (DebtId),
    CONSTRAINT FK_Debts_Clients FOREIGN KEY (ClientId) REFERENCES ledger.Clients (ClientId),
    CONSTRAINT CK_Debts_NonNegative CHECK (InterestDebt >= 0.00 AND ExpensesDebt >= 0.00 AND PrincipalDebt >= 0.00)
)
WITH 
(
    SYSTEM_VERSIONING = ON (HISTORY_TABLE = ledger.DebtsHistory),
    LEDGER = ON (APPEND_ONLY = OFF)
);

CREATE TABLE ledger.Overpayments
(
    OverpaymentId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
	TransactionId UNIQUEIDENTIFIER NOT NULL,
    ClientId UNIQUEIDENTIFIER NOT NULL,
    Amount DECIMAL(18, 2) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    SysStartTime DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEndTime DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStartTime, SysEndTime),
    CONSTRAINT PK_Overpayments PRIMARY KEY CLUSTERED (OverpaymentId),
    CONSTRAINT FK_Overpayments_Clients FOREIGN KEY (ClientId) REFERENCES ledger.Clients (ClientId),
    CONSTRAINT CK_Overpayments_Amount_Positive CHECK (Amount > 0.00)
)
WITH 
(
    SYSTEM_VERSIONING = ON (HISTORY_TABLE = ledger.OverpaymentsHistory),
    LEDGER = ON (APPEND_ONLY = OFF)
);

CREATE TABLE ledger.SuspenseTransactions
(
    TransactionId UNIQUEIDENTIFIER NOT NULL,
    TraceId CHAR(55) NOT NULL,
    FileReference NVARCHAR(150) NOT NULL,
    InboundEGN NVARCHAR(10) NOT NULL,
    InboundClientName NVARCHAR(255) NOT NULL,
    CorrectedEGN NVARCHAR(10) NULL,
    CorrectedClientName NVARCHAR(255) NULL,
    Amount DECIMAL(18, 2) NOT NULL,
    ReferenceNumber NVARCHAR(50) NOT NULL,
    SuspenseStatus INT NOT NULL DEFAULT 10,
    MakerUserId UNIQUEIDENTIFIER NULL,
    CheckerUserId UNIQUEIDENTIFIER NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL,
    SysStartTime DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEndTime DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStartTime, SysEndTime),
    CONSTRAINT PK_SuspenseTransactions PRIMARY KEY CLUSTERED (TransactionId),
    CONSTRAINT FK_SuspenseTransactions_InboundTransactions FOREIGN KEY (TransactionId) 
        REFERENCES staging.InboundTransactions (TransactionId),
    CONSTRAINT CK_SuspenseTransactions_Amount_Positive CHECK (Amount > 0.00),
    CONSTRAINT CK_SuspenseTransactions_MakerChecker_Distinct CHECK (
        MakerUserId IS NULL OR 
        CheckerUserId IS NULL OR 
        MakerUserId <> CheckerUserId
    )
)
WITH 
(
    SYSTEM_VERSIONING = ON (HISTORY_TABLE = ledger.SuspenseTransactionsHistory),
    LEDGER = ON (APPEND_ONLY = OFF)
);
GO


CREATE TABLE ledger.OutboxEvents
(
    EventId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EventType NVARCHAR(100) NOT NULL,
    Payload NVARCHAR(MAX) NOT NULL,
    IsProcessed INT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ProcessedAt DATETIME2 NULL,
    SysStartTime DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    SysEndTime DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStartTime, SysEndTime),
	TraceId CHAR(55) NOT NULL,
    CONSTRAINT PK_OutboxEvents PRIMARY KEY CLUSTERED (EventId)
)
WITH 
(
    SYSTEM_VERSIONING = ON (HISTORY_TABLE = ledger.OutboxEventsHistory),
    LEDGER = ON (APPEND_ONLY = OFF)
);


