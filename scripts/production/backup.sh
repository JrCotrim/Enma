#!/usr/bin/env bash
# Online, encrypted, off-site backup of the production host (D6, D29). The API
# keeps running. In order:
#   1. pg_dump -Fc of the database;
#   2. the documents bucket, mirrored with the pinned MinIO client as the
#      read-only backup identity (D36: list the bucket, read its objects);
#   3. the Data Protection key ring;
#   4. manifest.txt plus SHA256SUMS of every part.
# The parts are packed and encrypted with age for the configured recipient
# (the host never holds the private key), uploaded with a write-only
# credential to the S3-compatible destination outside the host, and the
# heartbeat is pinged on success and on failure.
#
# Upload path: a disposable container of the pinned MinIO client image on
# Docker's default bridge network reads the ciphertext from stdin. It never
# joins `backend`, and no service on `backend` gains egress.
#
# Retention belongs to the destination bucket's lifecycle rule (7 days); this
# script never lists, overwrites or deletes remote objects.
#
# Usage: sudo scripts/production/backup.sh (the enma-backup.timer runs it daily)
# Environment: ENMA_ENV_FILE (default /etc/enma/production.env),
#              ENMA_BACKUP_ENV_FILE (default /etc/enma/backup.env).

# shellcheck source=scripts/production/common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"

require_commands docker jq age curl sha256sum tar flock
backup_env_file=${ENMA_BACKUP_ENV_FILE:-/etc/enma/backup.env}
assert_secure_file "$ENMA_ENV_FILE"
assert_secure_file "$backup_env_file"

failure_url=$(env_value "$backup_env_file" ENMA_BACKUP_HEARTBEAT_FAILURE_URL)
success_url=$(require_env_value "$backup_env_file" ENMA_BACKUP_HEARTBEAT_SUCCESS_URL)
[[ $success_url == https://* && ($failure_url == https://* || -z $failure_url) ]] ||
    die "heartbeat URLs must use https"
recipient=$(require_env_value "$backup_env_file" ENMA_BACKUP_AGE_RECIPIENT)
[[ $recipient =~ ^age1[02-9ac-hj-np-z]{58}$ ]] || die "ENMA_BACKUP_AGE_RECIPIENT is not an age public key"
endpoint=$(require_env_value "$backup_env_file" ENMA_BACKUP_S3_ENDPOINT)
[[ $endpoint =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$ ]] ||
    die "ENMA_BACKUP_S3_ENDPOINT must be https://<host>[:port]"
bucket=$(require_env_value "$backup_env_file" ENMA_BACKUP_S3_BUCKET)
[[ $bucket =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || die "invalid ENMA_BACKUP_S3_BUCKET"
prefix=$(env_value "$backup_env_file" ENMA_BACKUP_S3_PREFIX)
[[ $prefix =~ ^([A-Za-z0-9._-]+/)*$ ]] || die "ENMA_BACKUP_S3_PREFIX must be empty or end with /"
access_key=$(require_env_value "$backup_env_file" ENMA_BACKUP_S3_ACCESS_KEY)
secret_key=$(require_env_value "$backup_env_file" ENMA_BACKUP_S3_SECRET_KEY)
work_root=${ENMA_BACKUP_WORK_DIR:-/var/tmp}

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
    if ((status != 0)); then
        log "BACKUP FAIL (exit $status)"
        [[ -n $failure_url ]] && { call_secret_url "$failure_url" || log "WARNING: failure heartbeat not delivered"; }
    fi
    exit "$status"
}
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

exec 9>/run/lock/enma-backup.lock  # outside PrivateTmp, shared with manual runs
flock --nonblock 9 || die "another backup is running"
work=$(mktemp -d "$work_root/enma-backup.XXXXXXXXXX")
chmod 700 -- "$work"
payload=$work/payload
mkdir -m 700 -- "$payload"

project=$(compose_project)
for service in postgres minio; do
    [[ -n $(running_service_container "$project" "$service") ]] || die "the $service service is not running"
done
root_user=$(require_env_value "$ENMA_ENV_FILE" ENMA_MINIO_ROOT_USER)
root_password=$(require_env_value "$ENMA_ENV_FILE" ENMA_MINIO_ROOT_PASSWORD)
[[ $root_user$root_password =~ ^[A-Za-z0-9._~-]+$ ]] ||
    die "the MinIO root credentials must be URL-safe (letters, digits, . _ ~ -)"
backup_access_key=$(require_env_value "$ENMA_ENV_FILE" ENMA_MINIO_BACKUP_ACCESS_KEY)
backup_secret_key=$(require_env_value "$ENMA_ENV_FILE" ENMA_MINIO_BACKUP_SECRET_KEY)
[[ $backup_access_key$backup_secret_key =~ ^[A-Za-z0-9._~-]+$ ]] ||
    die "the MinIO backup credentials must be URL-safe (letters, digits, . _ ~ -)"
[[ $backup_access_key != "$root_user" &&
    $backup_access_key != "$(require_env_value "$ENMA_ENV_FILE" ENMA_MINIO_APP_ACCESS_KEY)" &&
    $backup_secret_key != "$root_password" &&
    $backup_secret_key != "$(require_env_value "$ENMA_ENV_FILE" ENMA_MINIO_APP_SECRET_KEY)" ]] ||
    die "the MinIO backup identity must differ from the root and application identities"
unset root_user root_password backup_access_key backup_secret_key
toolbox=$(service_image postgres)
client=$(service_image minio-bootstrap)
started=$(date -u +%Y%m%dT%H%M%SZ)
name="enma-backup-$started-$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n').tar.age"

log "1/4 database dump"
compose exec -T postgres sh -ec 'exec pg_dump -Fc -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
    >"$payload/database.dump"
compose exec -T postgres pg_restore --list <"$payload/database.dump" >/dev/null ||
    die "the database dump is not a readable archive"
latest_migration=$(postgres_query <<<'SELECT max("MigrationId") FROM "__EFMigrationsHistory";')

log "2/4 documents"
temporary_volume=$(docker volume create --label enma.backup.temporary=true "enma-backup-${work##*.}")
compose run --rm --no-deps -T --volume "$temporary_volume:/backup" --entrypoint /bin/sh \
    minio-bootstrap -ec '
        export MC_HOST_source="http://$MINIO_BACKUP_ACCESS_KEY:$MINIO_BACKUP_SECRET_KEY@minio:9000"
        mkdir -p /backup/objects
        mc mirror --quiet "source/$DOCUMENTS_BUCKET" /backup/objects >/dev/null'
docker run --rm --network none --volume "$temporary_volume:/backup:ro" --entrypoint tar \
    "$toolbox" -C /backup/objects -cf - . >"$payload/documents.tar"
docker volume rm "$temporary_volume" >/dev/null
temporary_volume=''

log "3/4 Data Protection keys"
keys_volume=$(project_volume "$project" dataprotection_keys)
docker run --rm --network none --volume "$keys_volume:/keys:ro" --entrypoint tar \
    "$toolbox" -C /keys -cf - . >"$payload/dataprotection-keys.tar"

log "4/4 manifest"
image_tag=$(require_env_value "$ENMA_ENV_FILE" ENMA_IMAGE_TAG)
documents_listing=$(tar -tf "$payload/documents.tar")
keys_listing=$(tar -tf "$payload/dataprotection-keys.tar")
{
    printf 'format=1\n'
    printf 'created_utc=%s\n' "$started"
    printf 'image_tag=%s\n' "$image_tag"
    printf 'latest_migration=%s\n' "$latest_migration"
    printf 'document_objects=%s\n' "$(grep -c '[^/]$' <<<"$documents_listing" || true)"
    printf 'dataprotection_key_files=%s\n' "$(grep -c '[^/]$' <<<"$keys_listing" || true)"
} >"$payload/manifest.txt"
(cd -- "$payload" &&
    sha256sum database.dump documents.tar dataprotection-keys.tar manifest.txt >SHA256SUMS)

tar -C "$payload" -cf - SHA256SUMS manifest.txt database.dump documents.tar dataprotection-keys.tar |
    age --encrypt --recipient "$recipient" --output "$work/$name"
rm -rf -- "$payload"
size=$(stat -c %s -- "$work/$name")
digest=$(sha256sum -- "$work/$name" | cut -d ' ' -f 1)

log "uploading $prefix$name ($size bytes)"
MC_HOST_offsite="https://$(url_encode "$access_key"):$(url_encode "$secret_key")@${endpoint#https://}" \
    docker run --rm --interactive --network bridge --env MC_HOST_offsite "$client" \
    --quiet pipe "offsite/$bucket/$prefix$name" <"$work/$name" >/dev/null

call_secret_url "$success_url" || die "upload succeeded but the success heartbeat failed"
log "BACKUP PASS object=$prefix$name bytes=$size sha256=$digest migration=$latest_migration"
