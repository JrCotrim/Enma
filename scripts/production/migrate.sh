#!/usr/bin/env bash
# Applies pending EF Core migrations with the one-shot migrator (D14) of the
# release in ENMA_IMAGE_TAG. Fails closed when the API container is running:
# migrations run only while the API is stopped. deploy.sh calls it after
# stopping the API; it can also run alone during a maintenance window.
#
# Order (D34): the roles phase of deploy/postgres/enma-roles.sql (roles,
# passwords, database owner, conversion of existing objects to the migrator),
# the migrator connected as the migrator role, then the grants phase (owners
# and the application role's privileges, also for tables created now).
#
# Usage: sudo scripts/production/migrate.sh
# Environment: ENMA_ENV_FILE (default /etc/enma/production.env).

# shellcheck source=scripts/production/common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

require_commands docker
assert_secure_file "$ENMA_ENV_FILE"
project=$(compose_project)

assert_api_stopped "$project"
assert_database_identities
log "applying migrations (release $(require_env_value "$ENMA_ENV_FILE" ENMA_IMAGE_TAG))"
compose up --detach --wait postgres
database_roles roles
compose --profile migrate run --rm --no-TTY migrate
database_roles grants
log "MIGRATIONS PASS"
