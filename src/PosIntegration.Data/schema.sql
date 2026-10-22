-- Idempotent schema. Statements are separated by a line containing only GO.
-- Stock.Opening is the quantity loaded by import; OnHand = Opening - SUM(StockMovement.Applied) is an invariant that reconciliation checks.

IF OBJECT_ID(N'dbo.Stock', N'U') IS NULL
CREATE TABLE dbo.Stock (
    Sku     varchar(32) NOT NULL CONSTRAINT PK_Stock PRIMARY KEY,
    Opening int         NOT NULL CONSTRAINT CK_Stock_Opening CHECK (Opening >= 0),
    OnHand  int         NOT NULL CONSTRAINT CK_Stock_OnHand CHECK (OnHand >= 0)
);
GO

-- The primary key on SaleId is the idempotency guard: a second insert of the same sale fails with error 2627.
IF OBJECT_ID(N'dbo.ProcessedSale', N'U') IS NULL
CREATE TABLE dbo.ProcessedSale (
    SaleId      varchar(64)    NOT NULL CONSTRAINT PK_ProcessedSale PRIMARY KEY,
    Seq         bigint         NOT NULL,
    StoreId     varchar(32)    NOT NULL,
    OccurredAt  datetimeoffset NOT NULL,
    ProcessedAt datetime2      NOT NULL CONSTRAINT DF_ProcessedSale_ProcessedAt DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProcessedSale_Seq' AND object_id = OBJECT_ID(N'dbo.ProcessedSale'))
CREATE INDEX IX_ProcessedSale_Seq ON dbo.ProcessedSale (Seq);
GO

-- Quantity = units the POS sold, Applied = units taken from stock, Shortfall = Quantity - Applied (oversold or unknown SKU).
IF OBJECT_ID(N'dbo.StockMovement', N'U') IS NULL
CREATE TABLE dbo.StockMovement (
    Id       bigint      IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockMovement PRIMARY KEY,
    SaleId   varchar(64) NOT NULL CONSTRAINT FK_StockMovement_Sale REFERENCES dbo.ProcessedSale (SaleId),
    Sku      varchar(32) NOT NULL,
    Quantity int         NOT NULL CONSTRAINT CK_StockMovement_Quantity CHECK (Quantity > 0),
    Applied  int         NOT NULL,
    Shortfall AS (Quantity - Applied),
    CONSTRAINT CK_StockMovement_Applied CHECK (Applied >= 0 AND Applied <= Quantity),
    CONSTRAINT UQ_StockMovement_Sale_Sku UNIQUE (SaleId, Sku)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovement_Sku' AND object_id = OBJECT_ID(N'dbo.StockMovement'))
CREATE INDEX IX_StockMovement_Sku ON dbo.StockMovement (Sku) INCLUDE (Quantity, Applied);
GO

IF OBJECT_ID(N'dbo.SyncCursor', N'U') IS NULL
CREATE TABLE dbo.SyncCursor (
    Id      tinyint NOT NULL CONSTRAINT PK_SyncCursor PRIMARY KEY CONSTRAINT CK_SyncCursor_Single CHECK (Id = 1),
    LastSeq bigint  NOT NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.SyncCursor)
INSERT dbo.SyncCursor (Id, LastSeq) VALUES (1, 0);
GO

-- Launch guards a supervisor released at the terminal (see ADR 6). Written in the same transaction as the sale, so a duplicate delivery adds nothing.
IF OBJECT_ID(N'dbo.SaleOverride', N'U') IS NULL
CREATE TABLE dbo.SaleOverride (
    SaleId       varchar(64) NOT NULL CONSTRAINT FK_SaleOverride_Sale REFERENCES dbo.ProcessedSale (SaleId),
    Guard        varchar(32) NOT NULL,
    Code         varchar(64) NOT NULL,
    SupervisorId varchar(32) NOT NULL,
    CONSTRAINT PK_SaleOverride PRIMARY KEY (SaleId, Code)
);
GO
