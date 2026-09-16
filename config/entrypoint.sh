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
# Two distinct failure modes, and the second is the quiet one:
#   * a literal "$Name" reaching the app  -> this script did not run at all
#   * a config value that is unexpectedly EMPTY -> the variable was not set;
#     envsubst substitutes the empty string for an unset name rather than
#     leaving the literal. The UNSET report below names those.
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

unset_names=""
rendered_files=""

for src in /app/appsettings*.json; do
    [ -f "$src" ] || continue

    # Collect the placeholder names before rendering, so the ones that resolve
    # to nothing can be reported. Names match [A-Za-z_][A-Za-z0-9_]* and cannot
    # contain shell metacharacters, so the eval below is safe.
    for name in $(grep -oE '\$[A-Za-z_][A-Za-z0-9_]*' "$src" | sort -u | tr -d '$'); do
        eval "value=\${$name-__MAVERA_UNSET__}"
        if [ "$value" = "__MAVERA_UNSET__" ]; then
            case " $unset_names " in
                *" $name "*) ;;
                *) unset_names="$unset_names $name" ;;
            esac
        fi
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

if [ -n "$unset_names" ]; then
    # Not fatal: several placeholders are intentionally blank (the Okta and
    # mail secrets). But a value that is blank because its variable is spelled
    # differently in this repo than in x-placeholders looks identical from the
    # app's side, so name them all.
    echo "[entrypoint] UNSET (rendered as empty strings):" >&2
    for name in $unset_names; do
        echo "    \$$name" >&2
    done
fi

echo "[entrypoint] appsettings.json rendered; starting ${APP_DLL}"
exec dotnet "${APP_DLL}"
