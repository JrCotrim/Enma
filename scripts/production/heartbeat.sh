#!/usr/bin/env bash
# Outbound health heartbeat (D48). While the host has no public entry it
# stands in for the external uptime monitor of D7: every 5 minutes
# (enma-heartbeat.timer) the host checks itself and pings the external
# heartbeat monitor with the convention of backup.sh (D29): the success URL
# when every check passes; otherwise the failure URL (optional, but
# recommended), with only the reason codes of the failed checks as the body.
# A run that cannot check or cannot ping exits non-zero; the monitor alerts
# when no success arrives in time.
#
# Checks, independent of each other (reason code first):
#   edge_health       /health/ready answers 200 through the edge where this
#                     host reaches it (the Tailscale IPv4 bind address in
#                     tailnet mode, loopback in public mode), with TLS
#                     verified for ENMA_PUBLIC_HOST;
#   edge_certificate  tailnet only: the files in ENMA_EDGE_TLS_DIR pass the
#                     deploy preflight (check_tailnet_edge) with 14 days of
#                     margin, and so does the certificate the edge actually
#                     serves: it belongs to that key, names ENMA_PUBLIC_HOST
#                     and stays valid for 14 more days;
#   tailscale_state   tailnet only: `tailscale status --json` reports
#                     BackendState Running;
#   edge_firewall     tailnet only: the D51 rule is in place, by the deploy
#                     preflight itself (check_tailnet_firewall, read-only):
#                     Docker's iptables backend, the FORWARD jump to
#                     DOCKER-USER and the rule for ENMA_EDGE_BIND_ADDRESS;
#   disk_space        D53: the filesystems of the Docker data root and of the
#                     backup's working directory each keep at least 20% of
#                     their size available.
# Public mode runs edge_health and disk_space.
#
# The heartbeat URLs are capabilities: read from the environment file, handed
# to curl on stdin and never logged. Every network call has a short timeout,
# so a run ends within about a minute.
#
# Usage: sudo scripts/production/heartbeat.sh (enma-heartbeat.timer runs it every 5 minutes)
# Environment: ENMA_ENV_FILE (default /etc/enma/production.env).

# shellcheck source=scripts/production/common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

((EUID == 0)) || die "heartbeat.sh must run as root"
require_commands curl flock timeout df jq
assert_secure_file "$ENMA_ENV_FILE"

failure_url=$(env_value "$ENMA_ENV_FILE" ENMA_HEALTH_HEARTBEAT_FAILURE_URL)
success_url=$(require_env_value "$ENMA_ENV_FILE" ENMA_HEALTH_HEARTBEAT_SUCCESS_URL)
url_pattern='^https://[^"[:space:]]+$'
[[ $success_url =~ $url_pattern && ($failure_url =~ $url_pattern || -z $failure_url) ]] ||
    die "heartbeat URLs must use https"
host=$(require_env_value "$ENMA_ENV_FILE" ENMA_PUBLIC_HOST)
[[ $host =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] || die "ENMA_PUBLIC_HOST must be a host name"
resolve_edge_mode
address=$ENMA_RESOLVED_EDGE_ADDRESS
certificate_margin=$((14 * 86400))
[[ $ENMA_RESOLVED_EDGE_MODE != tailnet ]] ||
    require_commands tailscale openssl mktemp ip realpath stat

work=''
on_exit() {
    local status=$?
    trap - EXIT
    [[ -z $work ]] || rm -rf -- "$work"
    exit "$status"
}
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

exec 9>/run/lock/enma-heartbeat.lock  # outside PrivateTmp, shared with manual runs
flock --nonblock 9 || die "another heartbeat is running"

# The request goes to curl on stdin, like call_secret_url; no curl message is
# logged, since it would name the host.
edge_health() {
    local code status=0
    code=$(printf 'url = "https://%s/health/ready"\nresolve = "%s:443:%s"\n' "$host" "$host" "$address" |
        curl --silent --output /dev/null --max-time 5 --write-out '%{http_code}' --config -) || status=$?
    [[ $status == 0 && $code == 200 ]] && return 0
    log "edge_health: /health/ready through the edge failed (curl exit $status, HTTP ${code:-000})"
    return 1
}

edge_certificate() {
    local dir served=$work/served.crt
    check_tailnet_edge "$certificate_margin" ||
        { log "edge_certificate: the certificate files are not usable"; return 1; }
    dir=$(env_value "$ENMA_ENV_FILE" ENMA_EDGE_TLS_DIR)
    { timeout 5 openssl s_client -connect "$address:443" -servername "$host" </dev/null 2>/dev/null || true; } |
        openssl x509 -out "$served" 2>/dev/null ||
        { log "edge_certificate: the edge did not present a certificate"; return 1; }
    check_certificate_pair "$served" "$dir/tls.key" "$host" "$certificate_margin" ||
        { log "edge_certificate: the certificate the edge serves is not usable"; return 1; }
}

tailscale_state() {
    local state
    state=$({ timeout 5 tailscale status --json 2>/dev/null || true; } |
        jq -r '.BackendState // empty' 2>/dev/null) || state=''
    [[ $state == Running ]] && return 0
    [[ $state =~ ^[A-Za-z]{1,32}$ ]] || state=unknown
    log "tailscale_state: BackendState is $state, not Running"
    return 1
}

# check_tailnet_firewall runs in a child bash under a 5 s limit, since
# `docker info` and `iptables -w` would wait on a stuck daemon or xtables
# lock; the child inherits the resolved edge mode and logs its own reason.
edge_firewall() {
    local status=0
    ENMA_ENV_FILE=$ENMA_ENV_FILE timeout 5 bash -c 'source "$1" && check_tailnet_firewall' \
        bash "$ENMA_REPOSITORY_ROOT/scripts/production/common.sh" || status=$?
    ((status == 0)) && return 0
    log "edge_firewall: the D51 rule check failed (exit $status)"
    return 1
}

# D53: a full disk stops PostgreSQL and the backup, which writes a plaintext
# copy of everything before age encrypts it. The minimum share of a
# filesystem's size that must stay available (available / size, from df -P).
disk_space_minimum=20

# The Docker data root is `data-root` in /etc/docker/daemon.json, else
# Docker's default; the backup's working directory resolves as in backup.sh,
# and the PrivateTmp of its unit stays on that filesystem. One df for both
# under a short limit, since a stuck mount would block it; a filesystem that
# holds both is checked once. Mount points and percentages stay in the
# journal; anything that cannot be measured fails.
disk_space() {
    local docker_root=/var/lib/docker backup_root=${ENMA_BACKUP_WORK_DIR:-/var/tmp} configured output
    local device size used available mount seen='' lines=0 status=0
    if [[ -e /etc/docker/daemon.json ]]; then
        configured=$(jq -er '."data-root" // ""' /etc/docker/daemon.json 2>/dev/null) ||
            { log "disk_space: /etc/docker/daemon.json could not be read"; return 1; }
        [[ -z $configured ]] || docker_root=$configured
    fi
    output=$(timeout 2 df -P -- "$docker_root" "$backup_root" 2>/dev/null) ||
        { log "disk_space: df could not measure the Docker data root and the backup working directory"; return 1; }
    {
        read -r _
        while read -r device size used available _ mount; do
            lines=$((lines + 1))
            if [[ ! $size =~ ^[1-9][0-9]*$ || ! $available =~ ^[0-9]+$ ]]; then
                log "disk_space: df printed an unexpected line"
                return 1
            fi
            [[ $seen != *"|$device $size $used $available|"* ]] || continue
            seen+="|$device $size $used $available|"
            if ((available * 100 < size * disk_space_minimum)); then
                log "disk_space: $mount has $((available * 100 / size))% free, under the ${disk_space_minimum}% minimum"
                status=1
            fi
        done
    } <<<"$output"
    ((lines == 2)) || { log "disk_space: df printed an unexpected result"; return 1; }
    return "$status"
}

# One attempt of at most 10 s plus two retries, so a slow monitor cannot hold
# the run past the minute.
signal() {
    local status=0
    call_secret_url "$1" "${2-}" 10 2 2>/dev/null || status=$?
    ((status == 0)) || log "the heartbeat monitor did not accept the signal (curl exit $status)"
    return "$status"
}

checks=(edge_health)
if [[ $ENMA_RESOLVED_EDGE_MODE == tailnet ]]; then
    checks+=(edge_certificate tailscale_state edge_firewall)
    work=$(mktemp -d)
else
    log "public mode: edge_certificate, tailscale_state and edge_firewall skipped (tailnet only)"
fi
checks+=(disk_space)
reasons=()
for check in "${checks[@]}"; do
    "$check" || reasons+=("$check")
done

if ((${#reasons[@]} > 0)); then
    log "HEARTBEAT FAIL reasons=${reasons[*]}"
    if [[ -n $failure_url ]]; then
        signal "$failure_url" "${reasons[*]}" || log "WARNING: failure heartbeat not delivered"
    else
        log "WARNING: ENMA_HEALTH_HEARTBEAT_FAILURE_URL is not set; the monitor alerts when no success arrives"
    fi
    exit 1
fi
signal "$success_url" || die "every check passed, but the success heartbeat was not delivered"
log "HEARTBEAT PASS checks=${checks[*]}"
