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
# Standard Compose variable; the default is the Production topology.
export COMPOSE_FILE=${COMPOSE_FILE:-$ENMA_REPOSITORY_ROOT/deploy/compose.yaml:$ENMA_REPOSITORY_ROOT/deploy/compose.production.yaml}

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

compose() {
    docker compose --env-file "$ENMA_ENV_FILE" "$@"
}

compose_project() {
    local name
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

# Readiness through the edge on this host, with the public certificate
# verified: /health/ready must answer 200 and / must serve the HTML build.
wait_for_edge() {
    local host=$1 timeout=${2:-180} deadline content_type
    deadline=$((SECONDS + timeout))
    until curl --silent --fail --output /dev/null --max-time 5 \
        --resolve "$host:443:127.0.0.1" "https://$host/health/ready"; do
        ((SECONDS < deadline)) || die "https://$host/health/ready did not answer 200 within ${timeout}s"
        sleep 3
    done
    content_type=$(curl --silent --show-error --fail --output /dev/null --max-time 10 \
        --write-out '%{content_type}' --resolve "$host:443:127.0.0.1" "https://$host/")
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

# Calls a URL kept out of the process list: curl reads it from stdin.
call_secret_url() {
    local url=$1
    [[ $url != *'"'* ]] || die "invalid URL"
    printf 'url = "%s"\n' "$url" |
        curl --silent --show-error --fail --max-time 15 --retry 3 --output /dev/null --config -
}
