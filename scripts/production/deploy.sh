#!/usr/bin/env bash
# Deploys one ENMA release on the production host (D2, D14, D31). The release
# images enma-api:<tag> and enma-migrator:<tag> must already exist locally and
# <tag> must equal ENMA_IMAGE_TAG in the environment file, so every later
# Compose command uses the same release.
#
# Order: configuration checks, data services, storage bootstrap, API stop,
# migrations (migrate.sh refuses while the API runs), API and edge, then
# readiness and the HTML build through the edge.
#
# Rollback = set ENMA_IMAGE_TAG back to the previous tag printed here and run
# this script with it. Migrations are never reverted: the previous release
# must be compatible with the current forward schema, and its migrator finds
# nothing to apply.
#
# Usage: sudo scripts/production/deploy.sh <tag>
# Environment: ENMA_ENV_FILE (default /etc/enma/production.env).

# shellcheck source=scripts/production/common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

(($# == 1)) || die "usage: deploy.sh <tag>"
tag=$1
[[ $tag =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$ ]] || die "invalid image tag"

require_commands docker curl
assert_secure_file "$ENMA_ENV_FILE"

configured_tag=$(require_env_value "$ENMA_ENV_FILE" ENMA_IMAGE_TAG)
[[ $configured_tag == "$tag" ]] ||
    die "ENMA_IMAGE_TAG in $ENMA_ENV_FILE is '$configured_tag'; set it to '$tag' first"
if grep -Eq '^[A-Z0-9_]+=.*<[^>]*>' -- "$ENMA_ENV_FILE"; then
    die "$ENMA_ENV_FILE still contains <placeholder> values"
fi
if grep -Eiq 'include[[:space:]]*error[[:space:]]*detail' -- "$ENMA_ENV_FILE"; then
    die "$ENMA_ENV_FILE must not enable Include Error Detail"
fi
compose --profile migrate config --quiet || die "the Compose configuration is invalid"
for image in "enma-api:$tag" "enma-migrator:$tag"; do
    docker image inspect "$image" >/dev/null 2>&1 || die "image $image is not present on this host"
done

project=$(compose_project)
public_host=$(require_env_value "$ENMA_ENV_FILE" ENMA_PUBLIC_HOST)
previous_tag="unknown (no API container running)"
api_container=$(running_service_container "$project" api)
if [[ -n $api_container ]]; then
    previous_image=$(docker inspect --format '{{.Config.Image}}' "$api_container")
    previous_tag=${previous_image##*:}
fi
log "deploying release $tag; previous release: $previous_tag"

compose up --detach --wait postgres minio
run_storage_bootstrap "$project"
compose stop api
"$(dirname -- "${BASH_SOURCE[0]}")/migrate.sh"
compose up --detach --wait api caddy
wait_for_edge "$public_host"

log "DEPLOY PASS release=$tag"
if [[ -n $api_container ]]; then
    log "rollback: set ENMA_IMAGE_TAG=$previous_tag in $ENMA_ENV_FILE and run deploy.sh $previous_tag (migrations are not reverted)"
else
    log "no API container was running before this deploy: identify the previous release before any rollback"
fi
