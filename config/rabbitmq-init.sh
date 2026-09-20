#!/bin/sh
# Creates one RabbitMQ virtual host per project, so the services in a project
# publish to and consume from their own namespace and nobody else's. A vhost is
# a full namespace: exchanges, queues, bindings and messages in one are simply
# invisible from another, even though it is a single broker process.
#
# Why the management HTTP API and not rabbitmqctl: rabbitmqctl is an Erlang RPC
# client, so a sidecar needs the broker's Erlang cookie *and* its node name.
# Pinning the node name means setting `hostname:` on the broker, which moves the
# mnesia directory to rabbit@<newname> and orphans an existing rabbitmq-data
# volume -- the broker comes back looking empty. The API needs neither, and
# every call below is a PUT, so re-running on each deploy is idempotent.
#
# RABBITMQ_VHOSTS is a comma-separated list. Two entry shapes:
#
#   name                 the shared RABBITMQ_USER is granted full rights on it
#   name:user:password   a dedicated user is created with rights on THIS vhost
#                        only, and no management tag
#
# The second shape is what makes the boundary enforced rather than conventional:
# a project holding only those credentials cannot reach another project's vhost
# even if something points it at one. Set MessageBroker_Username/_Password in
# that project's app stack to the same pair -- see docker-compose.apps.yml.
set -eu

API="http://rabbitmq:15672/api"
ADMIN_USER="${RABBITMQ_USER:-mavera}"
ADMIN_PASS="${RABBITMQ_PASS:?RABBITMQ_PASS is not set}"

# Full rights on the vhost the permission is attached to, and only that vhost.
FULL='{"configure":".*","write":".*","read":".*"}'

trim() { printf '%s' "$1" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'; }

# Passwords legitimately contain \ and ", which would otherwise break the body.
json_escape() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

# Names go into the URL path unencoded, so keep them to characters that need no
# escaping. Rejecting early beats a 404 from a half-encoded path later.
check_name() {
  case $2 in
    '') echo "rabbitmq-init: empty $1 in entry '$3'" >&2; exit 1 ;;
    *[!A-Za-z0-9._-]*)
      echo "rabbitmq-init: $1 '$2' in entry '$3' has characters outside" >&2
      echo "               A-Z a-z 0-9 . _ - which this script does not encode" >&2
      exit 1 ;;
  esac
}

api() {  # api <METHOD> <PATH> [BODY]
  if [ -n "${3:-}" ]; then
    curl -fsS -u "$ADMIN_USER:$ADMIN_PASS" -X "$1" \
      -H 'content-type: application/json' -d "$3" "$API$2"
  else
    curl -fsS -u "$ADMIN_USER:$ADMIN_PASS" -X "$1" "$API$2"
  fi
}

# The broker's healthcheck is `check_running`, which goes green before the
# management plugin is listening on 15672. Wait for the API itself.
tries=0
until curl -fsS -u "$ADMIN_USER:$ADMIN_PASS" "$API/overview" >/dev/null 2>&1; do
  tries=$((tries + 1))
  if [ "$tries" -ge 60 ]; then
    echo "rabbitmq-init: no answer from $API after 60 tries" >&2
    exit 1
  fi
  sleep 2
done

VHOSTS=$(trim "${RABBITMQ_VHOSTS:-}")
if [ -z "$VHOSTS" ]; then
  echo "RABBITMQ_VHOSTS is empty - every project stays on the default vhost \"/\""
  echo "rabbitmq-init complete"
  exit 0
fi

OLDIFS=$IFS
IFS=','
for entry in $VHOSTS; do
  IFS=$OLDIFS
  entry=$(trim "$entry")
  [ -n "$entry" ] || continue

  vhost=$(trim "${entry%%:*}")
  rest=${entry#*:}
  [ "$rest" != "$entry" ] || rest=''          # no colon at all: bare vhost name
  user=$(trim "${rest%%:*}")
  pass=${rest#*:}
  [ "$pass" != "$rest" ] || pass=''           # only one colon: user, no password
  pass=$(trim "$pass")

  check_name "vhost" "$vhost" "$entry"
  api PUT "/vhosts/$vhost" '{}' >/dev/null
  echo "vhost ready: $vhost"

  if [ -z "$user" ]; then
    api PUT "/permissions/$vhost/$ADMIN_USER" "$FULL" >/dev/null
    echo "  shared user $ADMIN_USER granted full rights"
    continue
  fi

  check_name "username" "$user" "$entry"
  if [ "$user" = "$ADMIN_USER" ]; then
    # PUT /api/users rewrites tags, so this would silently demote the account
    # the management UI and this script itself log in with.
    echo "rabbitmq-init: '$user' is RABBITMQ_USER; use the bare '$vhost' form" >&2
    echo "               to grant it this vhost instead of redefining it" >&2
    exit 1
  fi
  [ -n "$pass" ] || { echo "rabbitmq-init: '$entry' names a user with no password" >&2; exit 1; }

  # Empty tags: no management UI, no access to any vhost it is not granted.
  api PUT "/users/$user" "{\"password\":\"$(json_escape "$pass")\",\"tags\":\"\"}" >/dev/null
  api PUT "/permissions/$vhost/$user" "$FULL" >/dev/null
  echo "  scoped user $user - this vhost only, no management access"
done
IFS=$OLDIFS

echo "rabbitmq-init complete"
