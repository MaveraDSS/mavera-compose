#!/bin/sh
# Creates one complete set of SQL Server catalogs per project, plus a login that
# can reach that set and nothing else on the instance. Same idea as the RabbitMQ
# vhosts: one server, but a boundary the server itself enforces, so a project
# holding only its own credentials cannot read another project's data even if
# something points it at it. The network is NOT the boundary -- every stack
# shares dokploy-network and `sqlserver` resolves from all of them.
#
# SQL_PROJECTS is a comma-separated list. Two entry shapes:
#
#   prefix                 the catalogs are created and restored; sa keeps
#                          access and no login is made. Conventional boundary.
#   prefix:login:password  a login is created that OWNS those catalogs (so it is
#                          dbo in each, which EF Core migrations need) and is
#                          denied everything else -- it cannot connect to, or
#                          even enumerate, another project's catalogs.
#
# FIELD 1 IS THE PREFIX VERBATIM, not a project slug: it is the identical string
# the app stack puts in DB_PREFIX, so the two halves cannot drift by a trailing
# hyphen. The convention is "<project>-". An empty first field means the
# unprefixed set, i.e. the names this stack used before projects existed.
#
# Why the per-project SQL is built here in shell and run with `sqlcmd -x`, while
# 00-init-databases.sql is the opposite (-v prefix, no -x): -x turns off
# sqlcmd's own $(...) preprocessor, so a password containing "$(" cannot be
# re-interpreted on its way to the server. The sql_str/sql_id helpers below are
# then the single escaping story for every value. 00-init-databases.sql carries
# no untrusted value, so it can use -v safely -- and needs to, since that is how
# it learns the prefix.
#
# Ordering: app stacks live in another Compose project and cannot depends_on
# this, so a project deployed before its catalogs exist crash-loops on "Cannot
# open database" until this has run. Same rule as rabbitmq-init and mongo-init:
# deploy infra first, let it settle.
set -eu

SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
SERVER="${SQL_SERVER:-sqlserver}"
: "${DB_PASS:?DB_PASS is not set}"

# Overridable for the same reason SQLCMD and BACKUP_DIR are in mssql-restore.sh:
# so the parsing and validation above the database can be exercised outside a
# container. Both are the paths mssql-init bind-mounts.
RESTORE_SH="${RESTORE_SH:-/mssql-restore.sh}"
INIT_DIR="${INIT_DIR:-/init}"

# The seven catalogs a project gets. Keep in step with the placeholders in
# docker-compose.apps.yml (the ${DB_PREFIX:-} ones) and with the two lists in
# 00-init-databases.sql. The three data-bearing ones come from the backups; the
# other four are created empty and the services migrate into them.
CATALOGS='vera-dev02 vera-caregivers-dev02 vera-identity-dev02 MaveraInboxOutbox MaveraScheduler MaveraStorageOperations MaveraOcrOperations'

# Prefix charset and ceiling are shared with mongo-init.js, because one
# DB_PREFIX feeds both engines. Mongo caps a database name at 63 bytes and its
# longest name is journalevents-classifier (24), so 38 is the real limit even
# though SQL Server would take far more. A longer prefix would break only the
# longest few Mongo databases -- the worst possible failure shape.
MAX_PREFIX=38

trim() { printf '%s' "$1" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'; }
die()  { echo "mssql-projects: $*" >&2; exit 1; }

# Escape a value for a T-SQL string literal.
sql_str() { printf '%s' "$1" | sed "s/'/''/g"; }

# Escape an identifier for [brackets].
sql_id() { printf '[%s]' "$(printf '%s' "$1" | sed 's/]/]]/g')"; }

# Run a batch with sqlcmd's variable preprocessor OFF. -b => fail the deploy.
run_sql() { "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -x -Q "SET NOCOUNT ON; $1"; }

# Split one list entry into $_prefix / $_login / $_pass (and $_entry, trimmed,
# for error messages). On the first two colons only, so a password may contain
# one -- same rule as rabbitmq-init.sh and mongo-init.js. sh has no arrays and
# no return values, hence the underscore-prefixed globals.
parse_entry() {
  _entry=$(trim "$1")
  _prefix=''; _login=''; _pass=''
  [ -n "$_entry" ] || return 0

  _prefix=$(trim "${_entry%%:*}")
  _rest=${_entry#*:}
  [ "$_rest" != "$_entry" ] || _rest=''       # no colon at all: bare prefix
  _login=$(trim "${_rest%%:*}")
  _pass=${_rest#*:}
  [ "$_pass" != "$_rest" ] || _pass=''        # only one colon: login, no password
  _pass=$(trim "$_pass")
}

check_prefix() {
  [ -n "$1" ] || return 0                      # empty is the unprefixed set
  case $1 in
    *[!a-z0-9_-]*)
      die "prefix '$1' in entry '$2' must be lowercase a-z 0-9 _ - only" ;;
    [!a-z0-9]*)
      die "prefix '$1' in entry '$2' must start with a letter or digit" ;;
  esac
  [ "${#1}" -le "$MAX_PREFIX" ] || \
    die "prefix '$1' is ${#1} characters; the limit is $MAX_PREFIX (Mongo caps a
               database name at 63 and the longest is journalevents-classifier)"
}

check_login() {
  case $1 in
    '') die "empty login in entry '$2'" ;;
    *[!A-Za-z0-9._-]*)
      die "login '$1' in entry '$2' must be A-Z a-z 0-9 . _ - only" ;;
  esac
  # CREATE LOGIN on sa would be a no-op but ALTER LOGIN would change the
  # password this very script authenticates with, mid-run.
  [ "$1" != "sa" ] || die "'sa' is the admin account; use the bare 'prefix' form
               to leave a project's catalogs under sa instead of redefining it"
}

# --- the scoped login --------------------------------------------------------

grant_login() {
  prefix="$1"; login="$2"; pass="$3"
  l_str="$(sql_str "$login")"; l_id="$(sql_id "$login")"

  # DEFAULT_DATABASE must already exist, which it does: the restore and
  # 00-init-databases.sql ran for this prefix immediately above.
  # CHECK_POLICY = ON means a weak password fails the deploy with a clear
  # error, which is the right outcome.
  run_sql "
    IF SUSER_ID(N'$l_str') IS NULL
        CREATE LOGIN $l_id WITH PASSWORD = N'$(sql_str "$pass")', CHECK_POLICY = ON,
            DEFAULT_DATABASE = $(sql_id "${prefix}vera-dev02");
    ELSE
        ALTER LOGIN $l_id WITH PASSWORD = N'$(sql_str "$pass")';

    /* No fixed server role, ever. Report and strip anything added by hand --
       sysadmin or securityadmin here would undo every DENY below. `public` is
       implicit and does not appear in sys.server_role_members. */
    DECLARE @r sysname, @s nvarchar(max);
    DECLARE role_cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT r.name FROM sys.server_role_members m
        JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
        JOIN sys.server_principals p ON p.principal_id = m.member_principal_id
        WHERE p.name = N'$l_str' AND r.name <> N'public';
    OPEN role_cur; FETCH NEXT FROM role_cur INTO @r;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        RAISERROR('  [warn]    stripping server role %s', 0, 1, @r) WITH NOWAIT;
        SET @s = N'ALTER SERVER ROLE ' + QUOTENAME(@r) + N' DROP MEMBER ' + QUOTENAME(N'$l_str') + N';';
        EXEC sp_executesql @s;
        FETCH NEXT FROM role_cur INTO @r;
    END
    CLOSE role_cur; DEALLOCATE role_cur;

    DENY CREATE ANY DATABASE TO $l_id;   /* no catalog outside the scheme      */
    DENY VIEW ANY DATABASE   TO $l_id;   /* cannot enumerate other projects    */
    DENY VIEW ANY DEFINITION TO $l_id;   /* nor see the other projects' logins */
  " > /dev/null

  for name in $CATALOGS; do
    db="${prefix}${name}"
    # guest is the one way a login with no user here could still get in, and a
    # restored backup can arrive with it enabled. This is the boundary, so it
    # is asserted on every deploy for every catalog we manage.
    #
    # Ownership, not db_owner: both give the DDL rights EF Core migrations
    # need, but under DENY VIEW ANY DATABASE a db_owner member sees only
    # master and tempdb in sys.databases, which confuses SSMS, health checks
    # and EF's database-existence probe. An owner sees the catalogs it owns.
    # ALTER AUTHORIZATION refuses if the login is already a user here, so
    # clear any same-named user a restore brought in first.
    run_sql "
      USE $(sql_id "$db");
      REVOKE CONNECT FROM guest;
      IF EXISTS (SELECT 1 FROM sys.database_principals
                 WHERE name = N'$l_str' AND type IN ('S','U','G'))
          DROP USER $l_id;
      USE [master];
      IF ISNULL(SUSER_SNAME((SELECT owner_sid FROM sys.databases
                             WHERE name = N'$(sql_str "$db")')), N'') <> N'$l_str'
          ALTER AUTHORIZATION ON DATABASE::$(sql_id "$db") TO $l_id;
    " > /dev/null
  done

  echo "  scoped login $login - owns these seven catalogs, denied every other"
}

# --- one project -------------------------------------------------------------

provision() {
  prefix="$1"
  echo "project: DB_PREFIX=${prefix:-<empty, the unprefixed set>}"
  DB_PREFIX="$prefix" /bin/sh "$RESTORE_SH"
  for f in "$INIT_DIR"/*.sql; do
    [ -f "$f" ] || continue
    echo "  applying $(basename "$f")"
    "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -v prefix="$prefix" -i "$f"
  done
}

inventory() {
  echo ""
  echo "Databases on this instance:"
  "$SQLCMD" -C -S "$SERVER" -U sa -P "$DB_PASS" -b -Q "SET NOCOUNT ON;
    SELECT name AS [database], state_desc AS [state], recovery_model_desc AS [recovery]
    FROM sys.databases WHERE database_id > 4 ORDER BY name;"
}

# --- main --------------------------------------------------------------------

# mssql-init only reaches this after `sqlserver` is healthy and the default pass
# has run, so a failure here is a real one rather than a race. Assert anyway, so
# an unreachable server does not look like a provisioning bug further down.
run_sql "SELECT 1;" > /dev/null

PROJECTS=$(trim "${SQL_PROJECTS:-}")
if [ -z "$PROJECTS" ]; then
  echo "SQL_PROJECTS is empty - one unprefixed set of catalogs, as before"
  inventory
  echo "mssql-projects complete"
  exit 0
fi

# Two passes over the same list. Everything is validated BEFORE anything is
# provisioned, because a restore is hundreds of megabytes and several minutes:
# finding the typo in the third entry after the first two have been restored
# leaves the instance half-built and the deploy failed. Same rule mongo-init.js
# follows. Re-parsing the list twice is the price, and it is nothing.
seen_prefixes=' '
seen_logins=' '

OLDIFS=$IFS
IFS=','
for entry in $PROJECTS; do
  IFS=$OLDIFS
  parse_entry "$entry"
  [ -n "$_entry" ] || continue

  check_prefix "$_prefix" "$_entry"
  case $seen_prefixes in
    *" ${_prefix:-<empty>} "*)
      die "prefix '${_prefix:-<empty>}' appears twice; the second pass would
               just re-provision the first project's catalogs" ;;
  esac
  seen_prefixes="$seen_prefixes${_prefix:-<empty>} "

  [ -n "$_login" ] || continue

  check_login "$_login" "$_entry"
  # Logins are server-wide. Reusing one across projects would hand that login
  # both sets of catalogs, which is an isolation break rather than a typo.
  case $seen_logins in
    *" $_login "*) die "login '$_login' is used by more than one project" ;;
  esac
  seen_logins="$seen_logins$_login "

  [ -n "$_pass" ] || die "'$_entry' names a login with no password"
done
IFS=$OLDIFS

IFS=','
for entry in $PROJECTS; do
  IFS=$OLDIFS
  parse_entry "$entry"
  [ -n "$_entry" ] || continue

  provision "$_prefix"

  if [ -z "$_login" ]; then
    echo "  no login in this entry - sa keeps access, boundary is conventional"
    continue
  fi
  grant_login "$_prefix" "$_login" "$_pass"
done
IFS=$OLDIFS

inventory
echo "mssql-projects complete"
