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
# SOURCE NAME vs TARGET NAME. $RESTORE_DATABASES lists the names *inside* the
# backups, which is also how the .bak files are named. $DB_PREFIX decides what
# they are restored AS, so one set of backups can populate one set of catalogs
# per Dokploy project:
#
#   DB_PREFIX=          vera-dev02*.bak  ->  [vera-dev02]            (as before)
#   DB_PREFIX=alpha-    vera-dev02*.bak  ->  [alpha-vera-dev02]
#
# config/mssql-projects.sh calls this once per project with DB_PREFIX set. The
# glob always uses the SOURCE name; everything else uses the target.
#
# For each source database in $RESTORE_DATABASES:
#   * find $BACKUP_DIR/<source>*.bak              (missing -> skip, not an error)
#   * restore if the target is absent, or exists but is an empty shell that
#     00-init-databases.sql created on an earlier deploy (no user tables), or
#     exists in a non-ONLINE state. A database with data is left alone unless
#     SQL_RESTORE_FORCE=true.
#   * read the logical file names with RESTORE FILELISTONLY and build one MOVE
#     clause per file, because the backups carry Windows paths that do not
#     exist on Linux and the logical names do NOT match the database names
#     (vera-dev02's data file is logically "vera2"; vera-identity-dev02's is
#     "vera-identity-test"). The physical paths are named after the TARGET, so
#     two projects restoring the same backup never collide on disk -- and the
#     restore refuses outright if a path it is about to write already belongs
#     to some other database.
#   * reset the owner if the SID the backup carried resolves to nothing on this
#     instance, and report any orphaned users it brought with it
#   * audit for 3-part references to the SOURCE name -- see below
#
# A restore that is attempted and fails is fatal: mssql-init exits non-zero and
# the stack does not come up on half-restored data.
#
# CROSS-DATABASE REFERENCES. A view, procedure or synonym inside the data that
# names another database explicitly ([vera-dev02].dbo.X) keeps pointing at the
# ORIGINAL name after a prefixed restore, because SQL Server gives no way to
# alias a database. The audit prints one [xdb] line per hit, and
# SQL_CROSSDB_STRICT=true turns that into a failed deploy. The two real fixes
# are to rewrite the reference from a config/mssql-init/10-*.sql fixup that
# itself uses $(prefix), or to leave that project on the empty prefix. For the
# three catalogs shipped here the audit is expected to find nothing: they are
# three different applications' data.
# =============================================================================
set -eu

SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
SERVER="${SQL_SERVER:-sqlserver}"
BACKUP_DIR="${BACKUP_DIR:-/var/opt/mssql/backups}"
DATA_DIR="${DATA_DIR:-/var/opt/mssql/data}"
FORCE="${SQL_RESTORE_FORCE:-false}"
STRICT="${SQL_CROSSDB_STRICT:-false}"

# Prepended to every target name. Empty is the single-project default and makes
# this script behave exactly as it did before per-project catalogs existed.
PREFIX="${DB_PREFIX:-}"

# Keep this list in step with the catalog placeholders in docker-compose.yml
# ($ConnectionStrings_DB_Name, $ConnectionStrings_CaregiverContext_DB_Name,
# $ConnectionString_DB_IdentityServer). These are SOURCE names: the prefix is
# applied on top, so the list does not change when a project is added. If it
# drifts, 00-init-databases.sql reports the database as [MISSING], so the
# mismatch is visible in the log.
DATABASES="${RESTORE_DATABASES:-vera-dev02 vera-caregivers-dev02 vera-identity-dev02}"

# Written by audit_xdb, read once at the end. A plain variable will not do: the
# audit runs inside a pipeline, hence a subshell, and an increment there is lost.
XDB_FLAG=/tmp/mssql-restore-xdb.$$

# --- helpers -----------------------------------------------------------------

# Every sqlcmd here gets </dev/null. Left to inherit, it takes whatever stdin
# its caller has -- inside restore_one's `while read ... done < "$filelist"`
# that is the file list itself, and that combination deadlocked mssql-init in
# anon_pipe_read with no child left to write the pipe. -Q never needs stdin.

# Run a statement, letting sqlcmd print its own output. -b => non-zero exit on error.
run_sql() {
    "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -Q "SET NOCOUNT ON; $1" </dev/null
}

# Run a statement and return the first bare value (no headers, trimmed).
query_value() {
    "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -h -1 -W \
        -Q "SET NOCOUNT ON; $1" </dev/null 2>/dev/null | tr -d '\r' | sed '/^$/d' | head -1
}

# Run a statement and return every non-empty row, one per line.
query_rows() {
    "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -h -1 -W \
        -Q "SET NOCOUNT ON; $1" </dev/null 2>/dev/null | tr -d '\r' | sed '/^$/d'
}

# Escape a value for a T-SQL string literal.
sql_str() { printf '%s' "$1" | sed "s/'/''/g"; }

# Escape an identifier for [brackets].
sql_id() { printf '[%s]' "$(printf '%s' "$1" | sed 's/]/]]/g')"; }

# --- post-restore fixups -----------------------------------------------------

# A backup carries the owner SID from the instance it was taken on. Here that
# SID usually resolves to nothing, which leaves the database effectively
# ownerless: the ALTER AUTHORIZATION in mssql-projects.sh, which hands the
# catalog to the project login, then fails in ways that read like a permissions
# bug rather than a stale SID. Reset it to sa and let that script take over.
reset_owner() {
    db="$1"; db_sql="$(sql_str "$db")"
    owner="$(query_value "
        SELECT ISNULL(SUSER_SNAME(owner_sid), N'')
        FROM sys.databases WHERE name = N'$db_sql';")"
    if [ -z "$owner" ]; then
        echo "  [owner]   $db had an unresolvable owner SID - set to sa"
        run_sql "ALTER AUTHORIZATION ON DATABASE::$(sql_id "$db") TO [sa];" > /dev/null
    fi
}

# Users the backup brought whose SID matches no login here. Nothing can log in
# as them, so they are harmless - but silently inheriting them is how a surprise
# turns up months later. Report, do not fix.
report_orphans() {
    db="$1"
    query_rows "
        USE $(sql_id "$db");
        SELECT name FROM sys.database_principals
        WHERE type = 'S' AND authentication_type = 1
          AND SUSER_SNAME(sid) IS NULL
          AND name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys');" \
    | while IFS= read -r u; do
        echo "  [orph]    $db: orphaned user '$u' carried in from the backup"
    done
}

# See "CROSS-DATABASE REFERENCES" in the header. Three probes, because none is
# complete on its own: sys.sql_expression_dependencies is the engine's own
# dependency catalog but misses dynamic SQL and WITH ENCRYPTION modules;
# synonyms are stored as strings and are not dependencies at all; searching the
# module text catches the rest, at the cost of the occasional false positive.
audit_xdb() {
    db="$1"; src="$2"
    hits="$(query_rows "
        USE $(sql_id "$db");
        SELECT DISTINCT N'dependency: ' + ISNULL(OBJECT_NAME(referencing_id), N'?')
                      + N' -> [' + referenced_database_name + N']'
        FROM sys.sql_expression_dependencies
        WHERE referenced_database_name IS NOT NULL
          AND referenced_database_name <> DB_NAME();
        SELECT N'synonym: ' + name + N' -> ' + base_object_name
        FROM sys.synonyms
        WHERE PARSENAME(base_object_name, 3) IS NOT NULL
          AND PARSENAME(base_object_name, 3) <> DB_NAME();
        SELECT N'module text: ' + OBJECT_SCHEMA_NAME(object_id) + N'.'
                                + OBJECT_NAME(object_id)
        FROM sys.sql_modules
        WHERE definition LIKE N'%' + REPLACE(N'$(sql_str "$src")', N'[', N'[[]') + N'%'
          AND OBJECT_NAME(object_id) IS NOT NULL;")"
    [ -n "$hits" ] || return 0
    printf '%s\n' "$hits" | while IFS= read -r h; do
        echo "  [xdb]     $db: $h"
    done
    : > "$XDB_FLAG"
}

# --- one database ------------------------------------------------------------

restore_one() {
    src="$1"                       # the name inside the backup, and its filename
    db="${PREFIX}${src}"           # what it is restored as
    db_sql="$(sql_str "$db")"

    # Newest matching backup wins, so a refreshed dump can just be dropped in.
    # Matched on the SOURCE name: the prefix is ours, not the backup's.
    bak=''
    for candidate in "$BACKUP_DIR/$src"*.bak; do
        [ -f "$candidate" ] || continue
        if [ -z "$bak" ] || [ "$candidate" -nt "$bak" ]; then
            bak="$candidate"
        fi
    done

    if [ -z "$bak" ]; then
        echo "  [skip]    $db - no backup matching $src*.bak in $BACKUP_DIR"
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

    if [ "$db" = "$src" ]; then
        echo "  [restore] $db  <-  $(basename "$bak")"
    else
        echo "  [restore] $db  <-  $(basename "$bak")  (source $src)"
    fi

    # RESTORE FILELISTONLY is parsed as text rather than INSERT ... EXEC: the
    # result set gains columns between SQL Server versions, which would make a
    # temp table with a fixed shape break on an image bump.
    filelist=/tmp/filelist.$$
    "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -h -1 -W -s '|' \
        -Q "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$(sql_str "$bak")';" \
        </dev/null | tr -d '\r' > "$filelist"

    # Every file already on the instance that belongs to some OTHER database,
    # one "physical_name|database" per line. Fetched once, up front, so the
    # loop below stays pure shell: it reads "$filelist" on stdin, and a sqlcmd
    # inside it is exactly what hung this script.
    taken="$(query_rows "
        SELECT physical_name + N'|' + DB_NAME(database_id) FROM sys.master_files
        WHERE DB_NAME(database_id) <> N'$db_sql';")"

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

        # Logical names are not guaranteed to be filesystem-safe. The TARGET
        # name goes into the path, which is what keeps two projects restoring
        # the same backup off each other's files.
        safe="$(printf '%s' "$logical" | tr -c 'A-Za-z0-9._-' '_')"
        phys="$DATA_DIR/${db}_${safe}.${ext}"

        # Tripwire. If that path is already a file of some OTHER database the
        # restore would overwrite live data. Nothing in the naming scheme should
        # ever produce this, which is exactly why it is worth asserting.
        # $taken already leaves out this database's own files.
        case "
$taken
" in
            *"
$phys|"*)
                owner_db=${taken#*"$phys|"}
                owner_db=${owner_db%%"
"*}
                echo "  [ERROR]   $db: $phys already belongs to database '$owner_db'" >&2
                rm -f "$filelist"
                return 1 ;;
        esac

        moves="$moves, MOVE N'$(sql_str "$logical")' TO N'$(sql_str "$phys")'"
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

    reset_owner "$db"
    report_orphans "$db"
    # Only meaningful under a prefix: restored under its own name, every 3-part
    # reference inside the data still resolves, which is the whole reason the
    # original names remain the default.
    [ -z "$PREFIX" ] || audit_xdb "$db" "$src"
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

rm -f "$XDB_FLAG"

if [ -n "$PREFIX" ]; then
    echo "restoring data-bearing databases from $BACKUP_DIR as ${PREFIX}*"
else
    echo "restoring data-bearing databases from $BACKUP_DIR"
fi
for src in $DATABASES; do
    restore_one "$src"
done

if [ -f "$XDB_FLAG" ]; then
    rm -f "$XDB_FLAG"
    if [ "$STRICT" = "true" ]; then
        echo "  [ERROR]   SQL_CROSSDB_STRICT=true and the audit found cross-database" >&2
        echo "            references that a prefixed restore does not repoint" >&2
        exit 1
    fi
    echo "  NOTE: [xdb] lines above are references a prefixed restore does not"
    echo "        repoint. See README 'Per-project SQL Server catalogs'."
fi

echo "restore step complete"
