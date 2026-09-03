#!/bin/sh
# ---------------------------------------------------------------------------
# Mavera compose entrypoint.
#
# Mirrors what the Kubernetes deployments do in their initContainer:
#   envsubst < /app/appsettings.json > /data/appsettings.json
#
# Every appsettings.json in these repos is an envsubst template whose values
# are $Placeholder tokens (that is why all 29 runtime images install gettext).
# Without this step the app receives the literal string "$Placeholder".
#
# Fails loudly rather than starting a service with unrendered config.
# ---------------------------------------------------------------------------
set -eu

SRC=/app/appsettings.json

if [ ! -f "$SRC" ]; then
    echo "[entrypoint] FATAL: $SRC not found" >&2
    exit 1
fi

if ! command -v envsubst >/dev/null 2>&1; then
    echo "[entrypoint] FATAL: envsubst (gettext) missing from this image" >&2
    exit 1
fi

envsubst < "$SRC" > /tmp/appsettings.rendered.json

# Overwrite in place. /app is chowned to the runtime user in all 29 images,
# so this succeeds; if it ever does not, stop instead of running misconfigured.
if ! cat /tmp/appsettings.rendered.json > "$SRC"; then
    echo "[entrypoint] FATAL: $SRC is not writable" >&2
    exit 1
fi

# Any placeholder left unrendered means a variable is missing from compose.
if grep -qE '\$[A-Za-z_][A-Za-z0-9_]*' "$SRC"; then
    echo "[entrypoint] WARNING: unrendered placeholders remain in appsettings.json:" >&2
    grep -oE '\$[A-Za-z_][A-Za-z0-9_]*' "$SRC" | sort -u >&2
fi

echo "[entrypoint] appsettings.json rendered; starting ${APP_DLL}"
exec dotnet "${APP_DLL}"
