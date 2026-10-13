#!/usr/bin/env bash
# Restores one encrypted backup made by backup.sh into an EMPTY production
# stack (D6: RTO 4 h). There is no force option: a database that already has
# relations, a bucket that already has objects or a non-empty key ring is
# refused. The API must be stopped.
#
# The age private key is supplied at run time and never written to disk: it is
# typed or pasted at the prompt, or read once from --identity (e.g. removable
# media), then handed to age through a pipe.
#
# Steps: decrypt, check the archive members and every SHA-256, start the data
# services, refuse non-empty targets, pg_restore, re-create the bucket and
# mirror the documents back, restore the Data Protection keys owned by the
# API user (uid 1654), start the stack, wait for /health/ready, and print a
# counts-only summary.
#
# Usage:
#   sudo scripts/production/restore.sh --file <backup.tar.age> [--identity <path>]
#   sudo -E scripts/production/restore.sh --object <key-in-bucket> [--identity <path>]
# --object downloads from the backup destination in ENMA_BACKUP_ENV_FILE with
# a read credential exported for this run only: ENMA_RESTORE_S3_ACCESS_KEY and
# ENMA_RESTORE_S3_SECRET_KEY (the host's write-only credential cannot read).
# Environment: ENMA_ENV_FILE (default /etc/enma/production.env),
#              ENMA_BACKUP_ENV_FILE (default /etc/enma/backup.env).

# shellcheck source=scripts/production/common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

readonly api_uid=1654
# Sorted in the C locale (common.sh sets LC_ALL=C).
readonly members=$'SHA256SUMS\ndatabase.dump\ndataprotection-keys.tar\ndocuments.tar\nmanifest.txt'

usage() { die "usage: restore.sh (--file <path> | --object <key>) [--identity <path>]"; }
source_file=''
source_object=''
identity_path=''
while (($# > 0)); do
    case $1 in
        --file) (($# >= 2)) || usage; source_file=$2; shift 2 ;;
        --object) (($# >= 2)) || usage; source_object=$2; shift 2 ;;
        --identity) (($# >= 2)) || usage; identity_path=$2; shift 2 ;;
        *) usage ;;
    esac
done
[[ -n $source_file || -n $source_object ]] && [[ -z $source_file || -z $source_object ]] || usage

require_commands docker jq age curl sha256sum tar sort comm
assert_secure_file "$ENMA_ENV_FILE"
project=$(compose_project)
assert_api_stopped "$project"

if [[ -n $identity_path ]]; then
    identity=$(<"$identity_path")
else
    read -rsp 'age identity (AGE-SECRET-KEY-1...): ' identity </dev/tty
    printf '\n' >&2
fi
[[ $identity == *AGE-SECRET-KEY-1* ]] || die "the identity is not an age private key"

work=''
temporary_volume=''
on_exit() {
    local status=$?
    trap - EXIT
    [[ -n $work ]] && rm -rf -- "$work"
    if [[ -n $temporary_volume ]]; then
        docker volume rm --force "$temporary_volume" >/dev/null 2>&1 ||
            log "WARNING: temporary volume $temporary_volume could not be removed"
    fi
    ((status == 0)) || log "RESTORE FAIL (exit $status)"
    exit "$status"
}
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

work=$(mktemp -d "${ENMA_RESTORE_WORK_DIR:-/var/tmp}/enma-restore.XXXXXXXXXX")
chmod 700 -- "$work"
payload=$work/payload
mkdir -m 700 -- "$payload"
toolbox=$(service_image postgres)
client=$(service_image minio-bootstrap)

if [[ -n $source_object ]]; then
    backup_env_file=${ENMA_BACKUP_ENV_FILE:-/etc/enma/backup.env}
    assert_secure_file "$backup_env_file"
    endpoint=$(require_env_value "$backup_env_file" ENMA_BACKUP_S3_ENDPOINT)
    bucket=$(require_env_value "$backup_env_file" ENMA_BACKUP_S3_BUCKET)
    [[ $endpoint =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$ ]] || die "invalid ENMA_BACKUP_S3_ENDPOINT"
    [[ $source_object =~ ^[A-Za-z0-9._/-]+\.tar\.age$ && $source_object != *..* ]] || die "invalid object key"
    [[ -n ${ENMA_RESTORE_S3_ACCESS_KEY:-} && -n ${ENMA_RESTORE_S3_SECRET_KEY:-} ]] ||
        die "export ENMA_RESTORE_S3_ACCESS_KEY and ENMA_RESTORE_S3_SECRET_KEY (read credential) first"
    log "downloading $source_object"
    MC_HOST_offsite="https://$(url_encode "$ENMA_RESTORE_S3_ACCESS_KEY"):$(url_encode "$ENMA_RESTORE_S3_SECRET_KEY")@${endpoint#https://}" \
        docker run --rm --network bridge --env MC_HOST_offsite "$client" \
        --quiet cat "offsite/$bucket/$source_object" >"$work/backup.tar.age"
    source_file=$work/backup.tar.age
fi
[[ -f $source_file ]] || die "backup file not found"

log "decrypting and verifying"
age --decrypt --identity <(printf '%s\n' "$identity") --output "$work/backup.tar" -- "$source_file" ||
    die "decryption failed (wrong identity or damaged file)"
unset identity
[[ $(tar -tf "$work/backup.tar" | sort) == "$members" ]] || die "unexpected archive contents"
tar -xf "$work/backup.tar" -C "$payload" --no-same-owner --no-same-permissions
rm -f -- "$work/backup.tar"
[[ $(cut -d ' ' -f 3 "$payload/SHA256SUMS" | sort) == "$(grep -v '^SHA256SUMS$' <<<"$members")" ]] ||
    die "SHA256SUMS does not cover exactly the backup parts"
(cd -- "$payload" && sha256sum --check --strict --quiet SHA256SUMS) || die "checksum mismatch"
backup_tag=$(env_value "$payload/manifest.txt" image_tag)
configured_tag=$(require_env_value "$ENMA_ENV_FILE" ENMA_IMAGE_TAG)
[[ $backup_tag == "$configured_tag" ]] ||
    die "the backup was taken on release '$backup_tag' but ENMA_IMAGE_TAG is '$configured_tag'; restore on the backup's release, then deploy"
docker image inspect "enma-api:$configured_tag" >/dev/null 2>&1 || die "image enma-api:$configured_tag is not present"
log "checksums verified; backup $(env_value "$payload/manifest.txt" created_utc), migration $(env_value "$payload/manifest.txt" latest_migration)"

log "starting data services and checking that every target is empty"
compose up --detach --wait postgres minio
relations=$(postgres_query <<<"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%'
      AND n.nspname NOT LIKE 'pg\_temp%';")
[[ $relations == 0 ]] || die "the target database is not empty ($relations relations); refusing to restore"
run_storage_bootstrap "$project"
bucket_objects() {
    compose run --rm --no-deps -T --entrypoint /bin/sh minio-bootstrap -ec '
        export MC_HOST_target="http://$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD@minio:9000"
        mc find "target/$DOCUMENTS_BUCKET"' | cut -d / -f 3-
}
existing_objects=$(bucket_objects)
[[ -z $existing_objects ]] || die "the target bucket is not empty; refusing to restore"
compose create --no-recreate api >/dev/null
keys_volume=$(project_volume "$project" dataprotection_keys)
existing_keys=$(docker run --rm --network none --volume "$keys_volume:/keys:ro" --entrypoint ls "$toolbox" -A /keys)
[[ -z $existing_keys ]] ||
    die "the Data Protection key volume is not empty; refusing to restore"

log "restoring the database"
compose exec -T postgres sh -ec 'exec pg_restore --exit-on-error --single-transaction --no-owner --no-privileges -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
    <"$payload/database.dump"

log "restoring documents"
temporary_volume=$(docker volume create --label enma.restore.temporary=true "enma-restore-${work##*.}")
docker run --rm --interactive --network none --volume "$temporary_volume:/restore" --entrypoint /bin/sh \
    "$toolbox" -ec 'mkdir /restore/objects && tar -x -C /restore/objects -f -' <"$payload/documents.tar"
compose run --rm --no-deps -T --volume "$temporary_volume:/restore:ro" --entrypoint /bin/sh \
    minio-bootstrap -ec '
        export MC_HOST_target="http://$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD@minio:9000"
        mc mirror --quiet /restore/objects "target/$DOCUMENTS_BUCKET" >/dev/null'
docker volume rm "$temporary_volume" >/dev/null
temporary_volume=''

log "restoring Data Protection keys"
docker run --rm --interactive --network none --volume "$keys_volume:/keys" --entrypoint /bin/sh \
    --env API_UID="$api_uid" "$toolbox" -ec \
    'tar -x -C /keys -f - && chown -R "$API_UID:$API_UID" /keys && chmod 700 /keys' \
    <"$payload/dataprotection-keys.tar"
keys_owner=$(docker run --rm --network none --volume "$keys_volume:/keys:ro" --entrypoint stat "$toolbox" -c %u /keys)
[[ $keys_owner == "$api_uid" ]] ||
    die "the Data Protection key directory is not owned by uid $api_uid"
rm -rf -- "$payload"

log "starting the stack"
public_host=$(require_env_value "$ENMA_ENV_FILE" ENMA_PUBLIC_HOST)
compose up --detach --wait api caddy
wait_for_edge "$public_host"

organizations=$(postgres_query <<<'SELECT count(*) FROM organizations;')
document_rows=$(postgres_query <<<'SELECT count(*) FROM legal_documents;')
pending_deletion=$(postgres_query <<<'SELECT count(*) FROM legal_documents WHERE deletion_requested_at IS NOT NULL;')
postgres_query <<<'SELECT stored_object_key FROM legal_documents;' | sort >"$work/rows"
bucket_objects | sort >"$work/objects"
missing=$(comm -23 "$work/rows" "$work/objects" | wc -l)
orphans=$(comm -13 "$work/rows" "$work/objects" | wc -l)
log "RESTORE PASS organizations=$organizations document_rows=$document_rows pending_deletion=$pending_deletion bucket_objects=$(wc -l <"$work/objects") rows_without_object=$missing objects_without_row=$orphans"
((missing == 0)) || log "WARNING: $missing document rows have no object (see the backup race in the runbook)"
