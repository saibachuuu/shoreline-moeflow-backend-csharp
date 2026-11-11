#!/usr/bin/env bash
# Pre-cutover drain check for the legacy Celery queues.
#
# The C# worker uses its own `csharp_job` collection and never consumes
# RabbitMQ (see docs/celery-cutover-analysis.md, option A: drain and cold
# switch). Before flipping traffic, every legacy queue this app produced into
# must be empty in both `ready` and `unacked` state.
#
# Usage:
#   export RABBITMQ_HOST=127.0.0.1
#   export RABBITMQ_USER=...  RABBITMQ_PASS=...      # never commit real values
#   ./tools/celery-drain-check.sh
#
# Optional:
#   RABBITMQ_MGMT_PORT  management plugin port (default 15672)
#   RABBITMQ_VHOST      vhost (default /)
#   CELERY_QUEUES       space separated queue list
#   CELERY_QUEUE_PREFIX module queue prefix to also check (e.g. "module.")
#
# Exit code: 0 when every queue is drained, 1 when any queue still has
# messages, 2 when the management API is unreachable.
set -euo pipefail

HOST=${RABBITMQ_HOST:-127.0.0.1}
PORT=${RABBITMQ_MGMT_PORT:-15672}
USER=${RABBITMQ_USER:-guest}
PASS=${RABBITMQ_PASS:-guest}
VHOST=${RABBITMQ_VHOST:-/}
QUEUES=${CELERY_QUEUES:-"default output security_email notification mit"}
PREFIX=${CELERY_QUEUE_PREFIX:-}

urlencode() {
  local s=$1 out="" c
  for (( i = 0; i < ${#s}; i++ )); do
    c=${s:i:1}
    case $c in
      [a-zA-Z0-9.~_-]) out+=$c ;;
      *) out+=$(printf '%%%02X' "'$c") ;;
    esac
  done
  printf '%s' "$out"
}

if ! command -v curl >/dev/null; then
  echo "curl is required" >&2
  exit 2
fi
if ! command -v jq >/dev/null; then
  echo "jq is required" >&2
  exit 2
fi

VHOST_ENC=$(urlencode "$VHOST")
BASE="http://${HOST}:${PORT}/api/queues/${VHOST_ENC}"

probe() {
  local queue=$1 payload
  if ! payload=$(curl -fsS -u "${USER}:${PASS}" "${BASE}/$(urlencode "$queue")" 2>/dev/null); then
    printf '%-24s %s\n' "$queue" "MISSING"
    return 0
  fi
  local ready unacked consumers
  ready=$(jq -r '.messages_ready // 0' <<<"$payload")
  unacked=$(jq -r '.messages_unacknowledged // 0' <<<"$payload")
  consumers=$(jq -r '.consumers // 0' <<<"$payload")
  printf '%-24s ready=%-6s unacked=%-6s consumers=%s\n' "$queue" "$ready" "$unacked" "$consumers"
  if [ "$ready" -ne 0 ] || [ "$unacked" -ne 0 ]; then
    return 1
  fi
  return 0
}

if ! curl -fsS -u "${USER}:${PASS}" "http://${HOST}:${PORT}/api/overview" >/dev/null 2>&1; then
  echo "cannot reach the RabbitMQ management API at ${HOST}:${PORT}" >&2
  exit 2
fi

pending=0
echo "vhost=${VHOST} host=${HOST}:${PORT}"
for q in $QUEUES; do
  probe "$q" || pending=$((pending + 1))
done

if [ -n "$PREFIX" ]; then
  echo "--- queues with prefix ${PREFIX}"
  while IFS= read -r q; do
    [ -z "$q" ] && continue
    probe "$q" || pending=$((pending + 1))
  done < <(curl -fsS -u "${USER}:${PASS}" "${BASE}" | jq -r --arg p "$PREFIX" '.[].name | select(startswith($p))')
fi

if [ "$pending" -eq 0 ]; then
  echo "DRAINED: safe to stop the legacy Celery workers"
  exit 0
fi

echo "PENDING: ${pending} queue(s) still hold messages; keep the legacy workers running" >&2
exit 1
