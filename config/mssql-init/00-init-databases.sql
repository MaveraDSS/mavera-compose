/* ===========================================================================
   Idempotent SQL Server bootstrap.

   Creates only the catalogs that are genuinely empty-by-design. The three
   data-bearing databases are restored from backup by config/mssql-restore.sh,
   which mssql-init runs immediately BEFORE this script -- so by the time this
   runs they should already exist, and it only reports on them. It deliberately
   does NOT create them: an empty database with one of those names would force
   the restore to use WITH REPLACE.

   Safe to run on every `docker compose up`: existing databases are never
   touched, and nothing here drops, replaces or alters anything.
   =========================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* ---------------------------------------------------------------------------
   1. Catalogs this stack owns and creates empty.
      MaveraInboxOutbox       -- MassTransit saga/outbox; schema comes from the
                                 services that migrate at startup
      MaveraScheduler         -- Hangfire creates its own schema
      MaveraStorageOperations -- storage-service
      MaveraOcrOperations     -- mavera-ocr
   --------------------------------------------------------------------------- */
DECLARE @owned TABLE (Ordinal int IDENTITY(1,1), DatabaseName sysname NOT NULL PRIMARY KEY);

INSERT INTO @owned (DatabaseName) VALUES
    (N'MaveraInboxOutbox'),        -- $ConnectionStrings_DB_InboxOutbox
    (N'MaveraScheduler'),          -- $ConnectionStrings_SchedulerContext_DB_Name
    (N'MaveraStorageOperations'),  -- $ConnectionStrings_DB_Storage_Operations
    (N'MaveraOcrOperations');      -- $ConnectionStrings_DB_Ocr_Operations

DECLARE @i int = 1, @n int = (SELECT COUNT(*) FROM @owned), @db sysname, @sql nvarchar(max);

WHILE @i <= @n
BEGIN
    SELECT @db = DatabaseName FROM @owned WHERE Ordinal = @i;

    IF DB_ID(@db) IS NOT NULL
        RAISERROR('  [skip]    %s already exists - not modified', 0, 1, @db) WITH NOWAIT;
    ELSE
    BEGIN
        RAISERROR('  [create]  %s (empty)', 0, 1, @db) WITH NOWAIT;
        SET @sql = N'CREATE DATABASE ' + QUOTENAME(@db) + N';';
        EXEC sp_executesql @sql;
    END

    SET @i += 1;
END

/* ---------------------------------------------------------------------------
   2. Databases restored from backup. Reported here, never created.

      config/mssql-restore.sh has already run at this point: it restores each
      of these from Databases.zip if the database is not already present, so
      [MISSING] here means no matching <db>*.bak was found (or the restore was
      turned off with SQL_RESTORE_ENABLED=false).

      See README "Restoring the data-bearing databases".
   --------------------------------------------------------------------------- */
DECLARE @expected TABLE (Ordinal int IDENTITY(1,1), DatabaseName sysname NOT NULL PRIMARY KEY, Placeholder varchar(64));

INSERT INTO @expected (DatabaseName, Placeholder) VALUES
    (N'vera-dev02',            'ConnectionStrings_DB_Name'),
    (N'vera-caregivers-dev02', 'ConnectionStrings_CaregiverContext_DB_Name'),
    (N'vera-identity-dev02',   'ConnectionString_DB_IdentityServer');

DECLARE @missing int = 0, @ph varchar(64);
SET @i = 1; SET @n = (SELECT COUNT(*) FROM @expected);

WHILE @i <= @n
BEGIN
    SELECT @db = DatabaseName, @ph = Placeholder FROM @expected WHERE Ordinal = @i;

    IF DB_ID(@db) IS NOT NULL
        RAISERROR('  [present] %s (restored)', 0, 1, @db) WITH NOWAIT;
    ELSE
    BEGIN
        SET @missing += 1;
        RAISERROR('  [MISSING] %s - no backup in Databases.zip, or repoint $%s', 0, 1, @db, @ph) WITH NOWAIT;
    END

    SET @i += 1;
END

IF @missing > 0
    RAISERROR('  NOTE: %d data-bearing database(s) missing. Services reading them will start but fail on their first query.', 0, 1, @missing) WITH NOWAIT;
GO

PRINT '';
PRINT 'Databases on this instance:';
SELECT name AS [database], state_desc AS [state], recovery_model_desc AS [recovery]
FROM sys.databases
WHERE database_id > 4
ORDER BY name;
GO
