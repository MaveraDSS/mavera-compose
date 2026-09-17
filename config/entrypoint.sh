#!/bin/sh
# ---------------------------------------------------------------------------
# Mavera entrypoint.
#
# Mirrors what the Kubernetes deployments do in their initContainer:
#   envsubst < /app/appsettings.json > /data/appsettings.json
#
# Every appsettings.json in these repos is an envsubst template whose values
# are $Placeholder tokens (that is why all 29 runtime images install gettext).
# Without this step the app receives the literal string "$Placeholder".
#
# Renders EVERY appsettings*.json, not just the base file. ASPNETCORE_ENVIRONMENT
# is set, so .NET also loads appsettings.$ASPNETCORE_ENVIRONMENT.json and lets it
# override the base. An unrendered override silently wins, and the symptom is a
# literal $Placeholder reaching the app from a file nothing ever touched.
#
# Also reconciles capitalisation. The repos are not consistent with each other:
# some templates spell a placeholder $log_Level and others $Log_Level, while
# x-placeholders can only define one spelling. envsubst matches names exactly
# and Linux environment variables are case-sensitive, so the other spelling
# would render as an empty string -- silently. For any placeholder this file
# uses that is unset, the first-letter case variant is tried before giving up.
#
# Two distinct failure modes, and the second is the quiet one:
#   * a literal "$Name" reaching the app -> this script did not run at all
#   * a config value that is unexpectedly EMPTY -> the variable was not set
#     under either spelling. The UNSET report below names those.
#
# Fails loudly rather than starting a service with unrendered config.
# ---------------------------------------------------------------------------
set -eu

BASE=/app/appsettings.json

if [ ! -f "$BASE" ]; then
    echo "[entrypoint] FATAL: $BASE not found" >&2
    exit 1
fi

if ! command -v envsubst >/dev/null 2>&1; then
    echo "[entrypoint] FATAL: envsubst (gettext) missing from this image" >&2
    exit 1
fi

# Flip the case of the first character: Log_Level <-> log_Level.
flip_first() {
    head=$(printf '%s' "$1" | cut -c1 | tr '[:upper:][:lower:]' '[:lower:][:upper:]')
    printf '%s%s' "$head" "$(printf '%s' "$1" | cut -c2-)"
}

unset_names=""
aliased_names=""
rendered_files=""

for src in /app/appsettings*.json; do
    [ -f "$src" ] || continue

    # Placeholder names cannot contain shell metacharacters (they match
    # [A-Za-z_][A-Za-z0-9_]*), so the evals below are safe.
    for name in $(grep -oE '\$[A-Za-z_][A-Za-z0-9_]*' "$src" | sort -u | tr -d '$'); do
        eval "value=\${$name-__MAVERA_UNSET__}"
        [ "$value" != "__MAVERA_UNSET__" ] && continue

        # Not set under this spelling. Try the other capitalisation before
        # treating it as missing.
        alt=$(flip_first "$name")
        eval "altvalue=\${$alt-__MAVERA_UNSET__}"
        if [ "$altvalue" != "__MAVERA_UNSET__" ]; then
            export "$name=$altvalue"
            case " $aliased_names " in
                *" $name "*) ;;
                *) aliased_names="$aliased_names $name<-$alt" ;;
            esac
            continue
        fi

        case " $unset_names " in
            *" $name "*) ;;
            *) unset_names="$unset_names $name" ;;
        esac
    done

    envsubst < "$src" > /tmp/appsettings.rendered.json

    # Overwrite in place. /app is chowned to the runtime user in all 29 images,
    # so this succeeds; if it ever does not, stop instead of running
    # misconfigured.
    if ! cat /tmp/appsettings.rendered.json > "$src"; then
        echo "[entrypoint] FATAL: $src is not writable" >&2
        exit 1
    fi
    rendered_files="$rendered_files $src"

    # Anything still matching means envsubst left it alone -- $$ or a $ not
    # followed by an identifier. Rare, but worth naming if it happens.
    if grep -qE '\$[A-Za-z_][A-Za-z0-9_]*' "$src"; then
        echo "[entrypoint] WARNING: unrendered placeholders remain in $src:" >&2
        grep -oE '\$[A-Za-z_][A-Za-z0-9_]*' "$src" | sort -u >&2
    fi
done

if [ -z "$rendered_files" ]; then
    echo "[entrypoint] FATAL: no appsettings*.json matched" >&2
    exit 1
fi

echo "[entrypoint] rendered:$rendered_files"

if [ -n "$aliased_names" ]; then
    # Informational: this repo spells these placeholders differently from
    # x-placeholders, and the other capitalisation supplied the value.
    echo "[entrypoint] case-matched:$aliased_names"
fi

if [ -n "$unset_names" ]; then
    # Not fatal: several placeholders are intentionally blank (the Okta, mail,
    # SMS and SMB secrets). But a value that is blank because nothing defines
    # it looks identical from the app's side, so name them all.
    echo "[entrypoint] UNSET (rendered as empty strings):" >&2
    for name in $unset_names; do
        echo "    \$$name" >&2
    done
fi

echo "[entrypoint] appsettings.json rendered; starting ${APP_DLL}"
exec dotnet "${APP_DLL}"
