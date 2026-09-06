/* ===========================================================================
   Idempotent SQL Server bootstrap.

   Guarantees that all seven databases EXIST, so every service can connect and
   the ones that carry EF migrations can build their own schema.

   Four are empty by design. The other three are data-bearing and are restored
   from backup by config/mssql-restore.sh, which mssql-init runs immediately
   BEFORE this script; whatever it could not restore (no backup supplied, or
   SQL_RESTORE_ENABLED=false) is created here as an empty database instead.

   Ordering is what makes that safe: the restore has already had its chance, so
   creating an empty database now cannot mask a backup. If a backup turns up on
   a later deploy, mssql-restore.sh sees a database with no user tables and
   restores over it.

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
   2. Data-bearing databases. Restored by config/mssql-restore.sh just before
      this script; created EMPTY here if that did not happen, so the database
      always exists and services can at least connect and migrate.

      [restored] the restore populated it (it has user tables)
      [empty]    it exists but has no tables -- no backup was supplied, so the
                 services that expect data will fail on their first real query
      [create]   it did not exist at all and was just created empty
   --------------------------------------------------------------------------- */
DECLARE @expected TABLE (Ordinal int IDENTITY(1,1), DatabaseName sysname NOT NULL PRIMARY KEY, Placeholder varchar(64));

INSERT INTO @expected (DatabaseName, Placeholder) VALUES
    (N'vera-dev02',            'ConnectionStrings_DB_Name'),
    (N'vera-caregivers-dev02', 'ConnectionStrings_CaregiverContext_DB_Name'),
    (N'vera-identity-dev02',   'ConnectionString_DB_IdentityServer');

DECLARE @unpopulated int = 0, @ph varchar(64), @tables int;
SET @i = 1; SET @n = (SELECT COUNT(*) FROM @expected);

WHILE @i <= @n
BEGIN
    SELECT @db = DatabaseName, @ph = Placeholder FROM @expected WHERE Ordinal = @i;

    IF DB_ID(@db) IS NULL
    BEGIN
        SET @unpopulated += 1;
        RAISERROR('  [create]  %s (empty - no backup restored; $%s)', 0, 1, @db, @ph) WITH NOWAIT;
        SET @sql = N'CREATE DATABASE ' + QUOTENAME(@db) + N';';
        EXEC sp_executesql @sql;
    END
    ELSE
    BEGIN
        SET @tables = 0;
        IF DATABASEPROPERTYEX(@db, 'Status') = 'ONLINE'
        BEGIN
            SET @sql = N'SELECT @c = COUNT(*) FROM ' + QUOTENAME(@db) + N'.sys.tables;';
            EXEC sp_executesql @sql, N'@c int OUTPUT', @c = @tables OUTPUT;
        END

        IF @tables > 0
            RAISERROR('  [restored] %s (%d tables)', 0, 1, @db, @tables) WITH NOWAIT;
        ELSE
        BEGIN
            SET @unpopulated += 1;
            RAISERROR('  [empty]   %s - exists but has no tables; no backup restored', 0, 1, @db) WITH NOWAIT;
        END
    END

    SET @i += 1;
END

IF @unpopulated > 0
    RAISERROR('  NOTE: %d data-bearing database(s) have no data. They exist, so services start and EF migrations run, but queries for seeded data will fail. Add the backups to Databases.zip and redeploy.', 0, 1, @unpopulated) WITH NOWAIT;
GO

PRINT '';
PRINT 'Databases on this instance:';
SELECT name AS [database], state_desc AS [state], recovery_model_desc AS [recovery]
FROM sys.databases
WHERE database_id > 4
ORDER BY name;
GO
