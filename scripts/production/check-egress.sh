#!/usr/bin/env bash
# Proves the internal `backend` network has no egress (D2, D18): a disposable
# container attached only to it must fail external DNS and TCP 443 to a
# public IP, and must reach PostgreSQL. Needs the postgres service running.
# Reads no secret; the environment file only renders the Compose model.
#
# Usage: scripts/production/check-egress.sh
# Environment: ENMA_ENV_FILE (default /etc/enma/production.env),
#              ENMA_EGRESS_PROBE_IP (default 1.1.1.1).

# shellcheck source=scripts/production/common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

require_commands docker jq
[[ -f $ENMA_ENV_FILE ]] || die "missing environment file: $ENMA_ENV_FILE"
probe_ip=${ENMA_EGRESS_PROBE_IP:-1.1.1.1}
[[ $probe_ip =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || die "invalid ENMA_EGRESS_PROBE_IP"

project=$(compose_project)
[[ -n $(running_service_container "$project" postgres) ]] || die "the postgres service is not running"
network=$(project_network "$project" backend)
[[ $(docker network inspect --format '{{.Internal}}' "$network") == true ]] ||
    die "the backend network is not internal"
image=$(service_image postgres)

# Exit status: 0 = isolated and PostgreSQL reachable; 10/11 = egress leak;
# 12 = PostgreSQL unreachable. The same DNS and TCP tools must first succeed
# against PostgreSQL, so a broken probe cannot pass as isolation.
# nslookup names are absolute (trailing dot): BusyBox nslookup appends the
# host's search domain, which Docker copies into the probe, and the embedded
# DNS of an internal network answers SERVFAIL for that unforwardable name.
set +e
docker run --rm --network "$network" --cap-drop ALL \
    --security-opt no-new-privileges:true --entrypoint /bin/sh \
    -e PROBE_IP="$probe_ip" "$image" -c '
        nslookup postgres. >/dev/null 2>&1 || exit 12
        nc -w 3 postgres 5432 </dev/null >/dev/null 2>&1 || exit 12
        pg_isready -q -t 5 -h postgres -p 5432 || exit 12
        if nslookup example.com. >/dev/null 2>&1; then exit 10; fi
        if nc -w 3 "$PROBE_IP" 443 </dev/null >/dev/null 2>&1; then exit 11; fi
        exit 0
    '
status=$?
set -e

case $status in
    0) log "EGRESS CHECK PASS: no external DNS, no TCP 443 to $probe_ip, PostgreSQL reachable" ;;
    10) die "EGRESS CHECK FAIL: external DNS resolves from the backend network" ;;
    11) die "EGRESS CHECK FAIL: TCP 443 to $probe_ip is reachable from the backend network" ;;
    12) die "EGRESS CHECK FAIL: PostgreSQL is not reachable on the backend network" ;;
    *) die "EGRESS CHECK FAIL: probe container exited with status $status" ;;
esac
