# shellcheck shell=bash
# Shared helpers for the ENMA production host scripts (D30). Sourced by
# deploy.sh, migrate.sh, backup.sh, restore.sh and check-egress.sh; never run
# directly. Secrets stay in the external environment files: they are read by
# Compose or exported to child processes, never passed as command-line
# arguments and never printed.

set -Eeuo pipefail
umask 077
export LC_ALL=C

ENMA_REPOSITORY_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
ENMA_ENV_FILE=${ENMA_ENV_FILE:-/etc/enma/production.env}
# COMPOSE_FILE is the standard Compose variable: when unset, resolve_edge_mode
# sets the Production topology for ENMA_EDGE_MODE; a caller's value is kept.

log() {
    printf '%s enma: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2
}

die() {
    log "ERROR: $*"
    exit 1
}

require_commands() {
    local command
    for command in "$@"; do
        command -v "$command" >/dev/null 2>&1 || die "required command not found: $command"
    done
}

# Environment files hold production secrets: a regular file owned by root
# with mode 600, or nothing runs.
assert_secure_file() {
    local path=$1 owner_mode
    [[ -f $path && ! -L $path ]] || die "missing environment file: $path"
    owner_mode=$(stat -c '%u %a' -- "$path")
    [[ $owner_mode == "0 600" ]] ||
        die "$path must be owned by root with mode 600 (found uid/mode $owner_mode)"
}

# Reads KEY=value from an environment file without sourcing it. Surrounding
# single or double quotes are removed; the last assignment wins, as in Compose.
env_value() {
    local path=$1 key=$2 value
    value=$(sed -n "s/^${key}=//p" -- "$path" | tail -n 1)
    value=${value%$'\r'}
    if [[ $value =~ ^\"(.*)\"$ || $value =~ ^\'(.*)\'$ ]]; then
        value=${BASH_REMATCH[1]}
    fi
    printf '%s' "$value"
}

require_env_value() {
    local path=$1 key=$2 value
    value=$(env_value "$path" "$key")
    [[ -n $value ]] || die "$key is not set in $path"
    printf '%s' "$value"
}

# 100.64.0.0/10, the Tailscale IPv4 range.
is_tailnet_ipv4() {
    local octet='(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])'
    [[ $1 =~ ^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.$octet\.$octet$ ]]
}

# D39: ENMA_EDGE_MODE in the environment file selects the edge. public = the
# Production files; tailnet = plus deploy/compose.tailnet.yaml (443/tcp on the
# node's Tailscale IPv4 address only). Read on first use, so every script's own
# checks of the environment file still come first; anything else fails closed.
# Sets ENMA_RESOLVED_EDGE_MODE and ENMA_RESOLVED_EDGE_ADDRESS (where this host
# reaches the edge), named apart from the variables Compose interpolates.
resolve_edge_mode() {
    [[ -z ${ENMA_RESOLVED_EDGE_MODE:-} ]] || return 0
    local mode files address=127.0.0.1 tls_dir
    mode=$(env_value "$ENMA_ENV_FILE" ENMA_EDGE_MODE)
    files=$ENMA_REPOSITORY_ROOT/deploy/compose.yaml:$ENMA_REPOSITORY_ROOT/deploy/compose.production.yaml
    case $mode in
        public) ;;
        tailnet)
            address=$(env_value "$ENMA_ENV_FILE" ENMA_EDGE_BIND_ADDRESS)
            tls_dir=$(env_value "$ENMA_ENV_FILE" ENMA_EDGE_TLS_DIR)
            is_tailnet_ipv4 "$address" ||
                die "ENMA_EDGE_BIND_ADDRESS must be the node's Tailscale IPv4 address (100.64.0.0/10)"
            [[ $tls_dir == /* ]] || die "ENMA_EDGE_TLS_DIR must be an absolute directory"
            files+=:$ENMA_REPOSITORY_ROOT/deploy/compose.tailnet.yaml
            ;;
        *) die "ENMA_EDGE_MODE must be public or tailnet in $ENMA_ENV_FILE" ;;
    esac
    if [[ -n ${COMPOSE_FILE:-} ]]; then
        log "edge mode $mode; Compose files (COMPOSE_FILE from the caller): $COMPOSE_FILE"
    else
        export COMPOSE_FILE=$files
        log "edge mode $mode; Compose files: $COMPOSE_FILE"
    fi
    export ENMA_RESOLVED_EDGE_MODE=$mode ENMA_RESOLVED_EDGE_ADDRESS=$address
}

# D40: checks an edge certificate and its key: both parse, the key belongs to
# the certificate, the certificate names <host> in a DNS subjectAltName and
# stays valid for <seconds> more. Logs the first problem and returns 1; never
# prints key material. Used by renew-cert.sh before a swap and by
# check_tailnet_edge.
check_certificate_pair() {
    local cert=$1 key=$2 host=$3 seconds=$4 cert_public key_public names
    cert_public=$(openssl x509 -in "$cert" -noout -pubkey 2>/dev/null) ||
        { log "the certificate is not a readable PEM certificate"; return 1; }
    key_public=$(openssl pkey -in "$key" -passin pass: -pubout 2>/dev/null </dev/null) ||
        { log "the key is not a readable, unencrypted PEM private key"; return 1; }
    [[ $cert_public == "$key_public" ]] ||
        { log "the certificate and the key do not belong together"; return 1; }
    # "X509v3 Subject Alternative Name:" then "DNS:a, DNS:b" on the next line.
    names=$(openssl x509 -in "$cert" -noout -ext subjectAltName 2>/dev/null) || names=''
    if [[ $names == *$'\n'* ]]; then names=${names#*$'\n'}; else names=''; fi
    names=${names//[[:space:]]/}
    [[ ,${names,,}, == *",dns:${host,,},"* ]] ||
        { log "the certificate subjectAltName does not name ENMA_PUBLIC_HOST"; return 1; }
    if ! openssl x509 -in "$cert" -noout -checkend "$seconds" >/dev/null 2>&1; then
        if ((seconds == 0)); then
            log "the certificate has expired"
        else
            log "the certificate has expired or expires within $((seconds / 86400)) days"
        fi
        return 1
    fi
}

# D40-D42 preflight of the tailnet edge, before anything starts; a no-op in
# public mode. ENMA_EDGE_TLS_DIR is a root-owned directory (700 or 750), and
# tls.crt (root, 644) and tls.key (root, 600) resolve to regular files inside
# it (renew-cert.sh keeps them as links into its current generation); the
# pair passes check_certificate_pair with at least <seconds> of validity left;
# and ENMA_EDGE_BIND_ADDRESS is assigned to an interface of this host. Logs
# the first problem and returns 1.
check_tailnet_edge() {
    local seconds=$1 host dir real_dir file path expected owner_mode addresses
    resolve_edge_mode
    [[ $ENMA_RESOLVED_EDGE_MODE == tailnet ]] || return 0
    require_commands openssl ip realpath stat
    host=$(require_env_value "$ENMA_ENV_FILE" ENMA_PUBLIC_HOST) || return 1
    dir=$(env_value "$ENMA_ENV_FILE" ENMA_EDGE_TLS_DIR)
    [[ -d $dir && ! -L $dir ]] || { log "tailnet preflight: $dir is not a directory"; return 1; }
    real_dir=$(realpath -e -- "$dir") || return 1
    owner_mode=$(stat -c '%u %g %a' -- "$dir") || return 1
    [[ $owner_mode == "0 0 700" || $owner_mode == "0 0 750" ]] ||
        { log "tailnet preflight: $dir must be root:root with mode 700 or 750 (found $owner_mode)"; return 1; }
    for file in tls.crt:644 tls.key:600; do
        expected=${file#*:}
        file=${file%%:*}
        if ! path=$(realpath -e -- "$dir/$file" 2>/dev/null) || [[ $path != "$real_dir"/* || ! -f $path ]]; then
            log "tailnet preflight: $dir/$file is missing or not a file inside $dir"
            return 1
        fi
        owner_mode=$(stat -c '%u %g %a' -- "$path") || return 1
        [[ $owner_mode == "0 0 $expected" ]] ||
            { log "tailnet preflight: $dir/$file must be root:root with mode $expected (found $owner_mode)"; return 1; }
    done
    check_certificate_pair "$dir/tls.crt" "$dir/tls.key" "$host" "$seconds" ||
        { log "tailnet preflight: the certificate in $dir is not usable"; return 1; }
    addresses=$(ip -4 -o address show) || return 1
    [[ $addresses == *" inet $ENMA_RESOLVED_EDGE_ADDRESS/"* ]] ||
        { log "tailnet preflight: ENMA_EDGE_BIND_ADDRESS is not assigned to any interface of this host"; return 1; }
    log "tailnet preflight passed"
}

# D51: in tailnet mode only tailscale0 may reach the edge. Docker publishes
# 443 with a DNAT that matches the bind address on any interface, ahead of
# ufw, so a packet for that address arriving on the public interface would
# reach Caddy. One rule at the top of DOCKER-USER (Docker's chain for host
# rules, evaluated before its own forwarding rules and kept across Docker
# restarts) drops it: the original destination, before the DNAT, comes from
# conntrack, and only original-direction packets match, so the replies of a
# tailnet connection pass. The host's own requests to the edge
# (wait_for_edge, heartbeat.sh) never traverse FORWARD. tailnet-firewall.sh
# installs the rule; the comment tags it.
ENMA_TAILNET_FIREWALL_COMMENT=enma-tailnet-edge

# Sets ENMA_TAILNET_FIREWALL_RULE to the D51 rule, without the chain.
tailnet_firewall_rule() {
    resolve_edge_mode
    ENMA_TAILNET_FIREWALL_RULE=(! -i tailscale0 -m conntrack --ctdir ORIGINAL
        --ctorigdst "$ENMA_RESOLVED_EDGE_ADDRESS/32"
        -m comment --comment "$ENMA_TAILNET_FIREWALL_COMMENT" -j DROP)
}

# D51 preflight, with Docker running; a no-op in public mode. Docker uses its
# default iptables firewall backend (the nftables backend has no
# DOCKER-USER), FORWARD jumps to DOCKER-USER, and the rule is there. Logs the
# first problem and returns 1.
check_tailnet_firewall() {
    local backend
    resolve_edge_mode
    [[ $ENMA_RESOLVED_EDGE_MODE == tailnet ]] || return 0
    require_commands docker iptables
    backend=$(docker info --format '{{.FirewallBackend.Driver}}' 2>/dev/null) || backend=''
    [[ $backend == iptables ]] ||
        { log "tailnet firewall: Docker's firewall backend is '${backend:-unknown}', not iptables"; return 1; }
    iptables -w -C FORWARD -j DOCKER-USER 2>/dev/null ||
        { log "tailnet firewall: FORWARD does not jump to DOCKER-USER"; return 1; }
    tailnet_firewall_rule
    iptables -w -C DOCKER-USER "${ENMA_TAILNET_FIREWALL_RULE[@]}" 2>/dev/null ||
        { log "tailnet firewall: the rule for ENMA_EDGE_BIND_ADDRESS is not in DOCKER-USER (run tailnet-firewall.sh)"; return 1; }
    log "tailnet firewall active"
}

compose() {
    resolve_edge_mode
    docker compose --env-file "$ENMA_ENV_FILE" "$@"
}

compose_project() {
    local name
    # Outside the redirect below, so a mode error stays visible.
    resolve_edge_mode
    name=$(compose config --no-interpolate 2>/dev/null | sed -n 's/^name: //p' | head -n 1)
    [[ -n $name ]] || die "could not resolve the Compose project name"
    printf '%s' "$name"
}

# The first running container of a Compose service, or nothing.
running_service_container() {
    local project=$1 service=$2
    docker ps --quiet \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=$service" | head -n 1
}

# Fails closed when any container of the API service (including one-off
# containers) is running, restarting or paused.
assert_api_stopped() {
    local project=$1 running
    running=$(running_service_container "$project" api) || die "could not list the API containers"
    [[ -z $running ]] || die "the API container is running; refusing to continue (stop it first)"
}

project_volume() {
    local project=$1 key=$2 names
    names=$(docker volume ls --quiet \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.volume=$key")
    [[ -n $names && $names != *$'\n'* ]] || die "expected exactly one '$key' volume in project $project"
    printf '%s' "$names"
}

project_network() {
    local project=$1 key=$2 ids
    ids=$(docker network ls --quiet \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.network=$key")
    [[ -n $ids && $ids != *$'\n'* ]] || die "expected exactly one '$key' network in project $project"
    printf '%s' "$ids"
}

# Pinned image of a Compose service (digest included).
service_image() {
    local image
    image=$(compose config --format json | jq -er --arg service "$1" '.services[$service].image') ||
        die "could not resolve the image of service $1"
    printf '%s' "$image"
}

# Runs SQL from stdin inside the PostgreSQL container with the credentials
# Compose already gave it; prints unaligned tuples only.
postgres_query() {
    compose exec -T postgres sh -ec \
        'exec psql -X -q -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
}

# D33/D34: the three PostgreSQL identities of the environment file. Role names
# match the SQL script's own check; the migrator and application passwords are
# URL-safe so the connection strings Compose builds stay intact. Fails closed on
# an invalid or reserved name, a reused name or a reused password.
assert_database_identities() {
    local superuser migrator app superuser_password migrator_password app_password value
    superuser=$(require_env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_USER)
    migrator=$(require_env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_MIGRATOR_USER)
    app=$(require_env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_APP_USER)
    superuser_password=$(require_env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_PASSWORD)
    migrator_password=$(require_env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_MIGRATOR_PASSWORD)
    app_password=$(require_env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_APP_PASSWORD)
    for value in "$migrator" "$app"; do
        [[ $value =~ ^[a-z_][a-z0-9_]{0,62}$ && $value != pg_* ]] ||
            die "ENMA_POSTGRES_MIGRATOR_USER and ENMA_POSTGRES_APP_USER must match ^[a-z_][a-z0-9_]{0,62}\$ and not start with pg_"
    done
    for value in "$migrator_password" "$app_password"; do
        [[ $value =~ ^[A-Za-z0-9._~-]{16,}$ ]] ||
            die "ENMA_POSTGRES_MIGRATOR_PASSWORD and ENMA_POSTGRES_APP_PASSWORD must be URL-safe (letters, digits, . _ ~ -) with at least 16 characters"
    done
    [[ $superuser != "$migrator" && $superuser != "$app" && $migrator != "$app" ]] ||
        die "the PostgreSQL superuser, migrator and application roles must be three different names"
    [[ $superuser_password != "$migrator_password" && $superuser_password != "$app_password" &&
        $migrator_password != "$app_password" ]] ||
        die "the PostgreSQL superuser, migrator and application passwords must all differ"
}

# D34: deploy/postgres/enma-roles.sql, run inside the postgres container as
# the bootstrap superuser over its local socket, then one call: `roles` before
# migrations and restores, `grants` after them. The role passwords are read
# from the environment file and reach psql only on stdin (printf is a shell
# builtin); a failure prints psql's error with them redacted.
database_roles() {
    local phase=$1 migrator app migrator_password='' app_password='' call output
    assert_database_identities
    migrator=$(env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_MIGRATOR_USER)
    app=$(env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_APP_USER)
    case $phase in
        roles)
            migrator_password=$(env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_MIGRATOR_PASSWORD)
            app_password=$(env_value "$ENMA_ENV_FILE" ENMA_POSTGRES_APP_PASSWORD)
            printf -v call "SELECT pg_temp.enma_apply_roles('%s', '%s', '%s', '%s');" \
                "$migrator" "$app" "$migrator_password" "$app_password"
            ;;
        grants)
            printf -v call "SELECT pg_temp.enma_apply_grants('%s', '%s');" "$migrator" "$app"
            ;;
        *) die "unknown database roles phase: $phase" ;;
    esac
    if ! output=$({ cat -- "$ENMA_REPOSITORY_ROOT/deploy/postgres/enma-roles.sql" &&
        printf '%s\n' "$call"; } |
        compose exec -T postgres sh -ec \
            'exec psql -X -q -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' 2>&1); then
        [[ -z $migrator_password ]] || output=${output//"$migrator_password"/[redacted]}
        [[ -z $app_password ]] || output=${output//"$app_password"/[redacted]}
        printf '%s\n' "$output" >&2
        die "database $phase failed"
    fi
    log "database $phase applied (migrator $migrator, application $app)"
}

# One-shot bootstrap: private bucket and restricted application identity.
run_storage_bootstrap() {
    local project=$1 container status
    compose up --detach --no-deps minio-bootstrap
    container=$(docker ps --all --quiet \
        --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=minio-bootstrap" | head -n 1)
    [[ -n $container ]] || die "the storage bootstrap container was not created"
    status=$(docker wait "$container")
    [[ $status == 0 ]] || die "the storage bootstrap failed (exit $status)"
}

# Readiness through the edge on this host, with the edge certificate
# verified: /health/ready must answer 200 and / must serve the HTML build.
# The host name resolves to where this host reaches the edge: loopback in
# public mode, the Tailscale IPv4 bind address in tailnet mode.
wait_for_edge() {
    local host=$1 timeout=${2:-180} deadline content_type address
    resolve_edge_mode
    address=$ENMA_RESOLVED_EDGE_ADDRESS
    deadline=$((SECONDS + timeout))
    until curl --silent --fail --output /dev/null --max-time 5 \
        --resolve "$host:443:$address" "https://$host/health/ready"; do
        ((SECONDS < deadline)) || die "https://$host/health/ready did not answer 200 within ${timeout}s"
        sleep 3
    done
    content_type=$(curl --silent --show-error --fail --output /dev/null --max-time 10 \
        --write-out '%{content_type}' --resolve "$host:443:$address" "https://$host/")
    [[ $content_type == text/html* ]] || die "https://$host/ did not return HTML"
    log "edge ready: /health/ready 200 and / serves HTML"
}

# Percent-encodes a value for use inside a URL userinfo component.
url_encode() {
    local value=$1 encoded='' character index
    for ((index = 0; index < ${#value}; index++)); do
        character=${value:index:1}
        case $character in
            [A-Za-z0-9.~_-]) encoded+=$character ;;
            *) printf -v character '%%%02X' "'$character"; encoded+=$character ;;
        esac
    done
    printf '%s' "$encoded"
}

# Calls a URL kept out of the process list: curl reads it from stdin. An
# optional <body> (lower-case letters, digits, _ and spaces: heartbeat reason
# codes) is sent as a POST. Each attempt lasts at most <seconds> (default 15)
# and is retried up to <retries> times (default 3) on a timeout or a
# transient HTTP error.
call_secret_url() {
    local url=$1 body=${2-} seconds=${3:-15} retries=${4:-3} body_pattern='^[a-z0-9_ ]*$'
    [[ $url != *'"'* ]] || die "invalid URL"
    [[ $body =~ $body_pattern ]] || die "invalid heartbeat body"
    {
        printf 'url = "%s"\n' "$url"
        [[ -z $body ]] || printf 'data-binary = "%s"\n' "$body"
    } | curl --silent --show-error --fail --max-time "$seconds" --retry "$retries" --output /dev/null --config -
}
