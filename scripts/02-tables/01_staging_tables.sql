USE JanusFinancialDb;
GO

IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_InboundTransactions_Idempotency' AND object_id = OBJECT_ID('staging.InboundTransactions'))
    DROP INDEX UX_InboundTransactions_Idempotency ON staging.InboundTransactions;
GO

IF OBJECT_ID('staging.InboundTransactions', 'U') IS NOT NULL
    DROP TABLE staging.InboundTransactions;
GO

IF OBJECT_ID('staging.ProcessedFiles', 'U') IS NOT NULL
    DROP TABLE staging.ProcessedFiles;
GO

CREATE TABLE staging.ProcessedFiles
(
    FileId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
	TraceId CHAR(55) NOT NULL,
    FileName NVARCHAR(255) NOT NULL,
    ProcessedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
	IsCleared BIT NOT NULL DEFAULT 0,
    CONSTRAINT PK_ProcessedFiles PRIMARY KEY CLUSTERED (FileId),
    CONSTRAINT UK_ProcessedFiles_FileName UNIQUE (FileName)
);
GO

CREATE TABLE staging.InboundTransactions
(
    TransactionId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    FileReference NVARCHAR(150) NOT NULL,
    RawEGN NVARCHAR(10) NOT NULL,
    RawClientName NVARCHAR(255) NULL,
    Amount DECIMAL(18, 2) NOT NULL,
    ReferenceNumber NVARCHAR(50) NOT NULL,
	TraceId CHAR(55) NOT NULL,
    Processed INT NOT NULL DEFAULT 0,
	CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RowIndex INT NOT NULL,
    CONSTRAINT PK_InboundTransactions PRIMARY KEY CLUSTERED (TransactionId)
);
GO

