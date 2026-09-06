#!/bin/sh
# =============================================================================
# Restore the data-bearing SQL Server databases from backups, idempotently.
#
# Runs inside mssql-init (which ships sqlcmd), against the `sqlserver` service.
# The `mssql-backups` volume is mounted at the SAME path in both containers, and
# both mounts are needed: this script globs the directory to find the backups,
# and the server process is what actually opens them, so the paths that go into
# the RESTORE statement have to resolve on both sides.
#
# For each database in $RESTORE_DATABASES:
#   * find $BACKUP_DIR/<db>*.bak                  (missing -> skip, not an error)
#   * restore if the database is absent, or exists but is an empty shell that
#     00-init-databases.sql created on an earlier deploy (no user tables), or
#     exists in a non-ONLINE state. A database with data is left alone unless
#     SQL_RESTORE_FORCE=true.
#   * read the logical file names with RESTORE FILELISTONLY and build one MOVE
#     clause per file, because the backups carry Windows paths that do not
#     exist on Linux and the logical names do NOT match the database names
#     (vera-dev02's data file is logically "vera2"; vera-identity-dev02's is
#     "vera-identity-test").
#
# A restore that is attempted and fails is fatal: mssql-init exits non-zero and
# the stack does not come up on half-restored data.
# =============================================================================
set -eu

SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
SERVER="${SQL_SERVER:-sqlserver}"
BACKUP_DIR="${BACKUP_DIR:-/var/opt/mssql/backups}"
DATA_DIR="${DATA_DIR:-/var/opt/mssql/data}"
FORCE="${SQL_RESTORE_FORCE:-false}"

# Keep this list in step with the catalog placeholders in docker-compose.yml
# ($ConnectionStrings_DB_Name, $ConnectionStrings_CaregiverContext_DB_Name,
# $ConnectionString_DB_IdentityServer). If it drifts, 00-init-databases.sql
# reports the database as [MISSING], so the mismatch is visible in the log.
DATABASES="${RESTORE_DATABASES:-vera-dev02 vera-caregivers-dev02 vera-identity-dev02}"

# --- helpers -----------------------------------------------------------------

# Run a statement, letting sqlcmd print its own output. -b => non-zero exit on error.
run_sql() {
    "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -Q "SET NOCOUNT ON; $1"
}

# Run a statement and return the first bare value (no headers, trimmed).
query_value() {
    "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -h -1 -W \
        -Q "SET NOCOUNT ON; $1" 2>/dev/null | tr -d '\r' | sed '/^$/d' | head -1
}

# Escape a value for a T-SQL string literal.
sql_str() { printf '%s' "$1" | sed "s/'/''/g"; }

# Escape an identifier for [brackets].
sql_id() { printf '[%s]' "$(printf '%s' "$1" | sed 's/]/]]/g')"; }

# --- one database ------------------------------------------------------------

restore_one() {
    db="$1"
    db_sql="$(sql_str "$db")"

    # Newest matching backup wins, so a refreshed dump can just be dropped in.
    bak=''
    for candidate in "$BACKUP_DIR/$db"*.bak; do
        [ -f "$candidate" ] || continue
        if [ -z "$bak" ] || [ "$candidate" -nt "$bak" ]; then
            bak="$candidate"
        fi
    done

    if [ -z "$bak" ]; then
        echo "  [skip]    $db - no backup matching $db*.bak in $BACKUP_DIR"
        return 0
    fi

    # -1 absent | 0 present but empty or not ONLINE | N present with N user tables.
    # The empty case matters: 00-init-databases.sql creates these databases empty
    # when no backup was available, so without it a backup added later would be
    # skipped forever on the grounds that "the database already exists".
    status="$(query_value "
        DECLARE @db sysname = N'$db_sql';
        IF DB_ID(@db) IS NULL
            SELECT -1;
        ELSE IF DATABASEPROPERTYEX(@db, 'Status') <> 'ONLINE'
            SELECT 0;
        ELSE
        BEGIN
            DECLARE @cnt int,
                    @stmt nvarchar(max) = N'SELECT @c = COUNT(*) FROM '
                                        + QUOTENAME(@db) + N'.sys.tables;';
            EXEC sp_executesql @stmt, N'@c int OUTPUT', @c = @cnt OUTPUT;
            SELECT @cnt;
        END")"

    replace=''
    case "$status" in
        -1)
            : ;;                       # absent: plain restore
        0)
            echo "  [empty]   $db exists but is empty - restoring over it"
            replace=', REPLACE' ;;
        *)
            if [ "$FORCE" != "true" ]; then
                echo "  [skip]    $db already exists with data ($status tables) - not restored"
                return 0
            fi
            echo "  [REPLACE] $db has data and SQL_RESTORE_FORCE=true - overwriting"
            replace=', REPLACE' ;;
    esac

    echo "  [restore] $db  <-  $(basename "$bak")"

    # RESTORE FILELISTONLY is parsed as text rather than INSERT ... EXEC: the
    # result set gains columns between SQL Server versions, which would make a
    # temp table with a fixed shape break on an image bump.
    filelist=/tmp/filelist.$$
    "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -h -1 -W -s '|' \
        -Q "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$(sql_str "$bak")';" \
        | tr -d '\r' > "$filelist"

    moves=''
    data_files=0
    total_files=0

    while IFS='|' read -r logical physical type _rest || [ -n "${logical:-}" ]; do
        [ -n "${logical:-}" ] || continue
        # Guard against any stray sqlcmd chatter that is not a file row.
        case "$type" in
            D|L|F|S) : ;;
            *) continue ;;
        esac

        case "$type" in
            D)  data_files=$((data_files + 1))
                if [ "$data_files" -eq 1 ]; then ext=mdf; else ext=ndf; fi ;;
            L)  ext=ldf ;;
            *)  echo "  [ERROR]   $db: unsupported backup file type '$type' ($logical)" >&2
                rm -f "$filelist"
                return 1 ;;
        esac

        # Logical names are not guaranteed to be filesystem-safe.
        safe="$(printf '%s' "$logical" | tr -c 'A-Za-z0-9._-' '_')"
        target="$DATA_DIR/${db}_${safe}.${ext}"

        moves="$moves, MOVE N'$(sql_str "$logical")' TO N'$(sql_str "$target")'"
        total_files=$((total_files + 1))
    done < "$filelist"

    rm -f "$filelist"

    if [ "$total_files" -eq 0 ]; then
        echo "  [ERROR]   $db: RESTORE FILELISTONLY returned no files from $bak" >&2
        return 1
    fi

    run_sql "RESTORE DATABASE $(sql_id "$db") FROM DISK = N'$(sql_str "$bak")'
             WITH RECOVERY, STATS = 20$replace$moves;"

    state="$(query_value "SELECT state_desc FROM sys.databases WHERE name = N'$db_sql';")"
    if [ "$state" != "ONLINE" ]; then
        echo "  [ERROR]   $db restored but state is '${state:-unknown}', expected ONLINE" >&2
        return 1
    fi
    echo "  [ok]      $db ONLINE ($total_files file(s) relocated to $DATA_DIR)"
}

# --- main --------------------------------------------------------------------

if [ "${SQL_RESTORE_ENABLED:-true}" != "true" ]; then
    echo "restore disabled (SQL_RESTORE_ENABLED=${SQL_RESTORE_ENABLED:-true})"
    exit 0
fi

if [ ! -d "$BACKUP_DIR" ]; then
    echo "no backup directory at $BACKUP_DIR - nothing to restore"
    exit 0
fi

# Fail loudly here rather than letting an unreachable server look like "database
# does not exist" further down: query_value pipes sqlcmd, which would swallow it.
run_sql "SELECT 1;" > /dev/null

echo "restoring data-bearing databases from $BACKUP_DIR"
for db in $DATABASES; do
    restore_one "$db"
done
echo "restore step complete"
