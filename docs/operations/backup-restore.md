# Backup and restore

This runbook is the Closed Beta baseline for ENMA's PostgreSQL database and
private document bucket. It proves a provider-neutral procedure; it does not
mean that production-provider backups have been enabled.

## Policy

- Recovery point objective (RPO): at most 24 hours.
- Schedule: one quiesced backup every day.
- Minimum retention: the seven most recent daily backups.
- Restore drill: before the first beta, after a structural database or storage
  change, and periodically during beta.
- Access: only operators who need recovery access. Backup locations contain
  sensitive legal and identity data and require encryption at rest, restricted
  access, audited access where available, and encrypted transport.

Retention deletion is intentionally not automated by these scripts. Apply the
provider's reviewed lifecycle policy after confirming that it retains at least
seven successful daily backups.

## Consistency model

Backup consistent requires API without writers during the window. Stop only
the identified `Enma.Api` process before backup. This also stops the in-process
notification worker. Do not stop Docker Desktop, PostgreSQL, MinIO, editors, or
unrelated processes. Keep the API stopped until `BACKUP PASS` is reported, then
restart only the same API process through its normal launch mechanism.

The backup script fails if `Enma.Api` is detected and requires the explicit
`-ConfirmQuiesced` acknowledgement. PostgreSQL and the object bucket are read
without mutation. Source evidence is captured before and after the copy and
must remain identical.

`legal_documents` is the authoritative metadata store for object key, content
type, byte length, and SHA-256. The application writes and reads object bytes
but does not depend on additional S3 object metadata, so the portable object
backup preserves the complete key/byte tree while the database dump preserves
the metadata contract.

## Local prerequisites

- PowerShell 5.1 or later and Docker Engine must be available.
- `enma-postgres` and `enma-minio` must be healthy.
- The source database and bucket configuration must be present in their
  containers; credentials are not passed on the command line or written to the
  manifest.
- The backup destination must be outside the repository. The default is
  `%TEMP%\Enma\backups`.
- Each generated backup, report, and temporary credential directory has ACL
  inheritance removed and grants access only to the current operator, SYSTEM,
  and local Administrators.
- Confirm enough free space for a custom-format database dump, all objects, and
  temporary drill copies.

## Create and validate a backup

1. Open the maintenance window and stop only `Enma.Api`.
2. Run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File `
     ".\scripts\operations\Backup-Enma.ps1" -ConfirmQuiesced
   ```

3. Require `BACKUP PASS`. Record the backup directory, dump SHA-256, object
   count, and total bytes. A timestamped directory contains:

   - `database.dump`: PostgreSQL custom format, created by `pg_dump -Fc`;
   - `objects/`: complete bucket key/byte tree;
   - `manifest.json`: versions, migration, counts, sizes, SHA-256 inventory,
     source evidence, and no credentials.

4. Move or replicate the completed directory to approved encrypted backup
   storage. Never place a dump, object tree, manifest from real data, or drill
   report in Git.
5. Restart the API using its normal launch mechanism and close the maintenance
   window.

Any failure returns non-zero and removes the incomplete timestamped payload so
that sensitive partial dumps are not retained. Correct the cause and run a new
backup; never overwrite an existing backup.

## Run an isolated restore drill

The drill creates fresh, randomly named containers and volumes containing
`restore-drill`. It never publishes ports, never uses `enma-postgres`,
`enma-minio`, `enma_postgres_data`, `enma_minio_data`, or database `enma`, and
removes only resources bearing the drill's ownership label.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\operations\Test-EnmaRestore.ps1" `
  -BackupPath "C:\approved\path\to\enma-YYYYMMDDTHHMMSSZ-id"
```

The script validates the manifest and dump hash before creating infrastructure,
proves that the PostgreSQL target has no application tables and the target
bucket is absent, restores both stores, and writes `report.json` under
`%TEMP%\Enma\restore-drills`. It then verifies:

- latest EF migration and relevant database counts;
- all restored foreign keys are validated;
- 100% object keys, sizes, and SHA-256 values match the backup;
- every `legal_documents.stored_object_key` has an object;
- object size and SHA-256 match `size_bytes` and `content_hash_sha256`;
- no orphan metadata and no orphan objects;
- at least one restored document has a valid PDF, PNG, JPEG, DOCX, or XLSX
  signature/package shape;
- source container IDs, migration, counts, and bucket listing remain unchanged.

Require `RESTORE DRILL PASS` and `overall: PASS` in the report. By default,
isolated containers and volumes are deleted after validation; the report and
backup remain. Use `-KeepDrillResources` only for a time-bounded investigation,
then remove exactly the named, labelled drill resources. Never use global prune
commands or `docker compose down -v`.

Run guard checks after script changes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\operations\Test-BackupRestoreGuards.ps1"
```

They prove fail-closed behavior for missing/invalid manifests, missing dumps,
dump hash mismatch, missing object backups, an original target name, an
unavailable source container, and an unavailable restore dependency.

## Disaster restore

The drill script is deliberately not a production in-place restore tool. For a
real incident:

1. Declare the incident, prevent application writes, preserve the failed
   environment for investigation, and select a completed backup whose manifest
   and hashes validate.
2. Provision a separate PostgreSQL instance and separate private object bucket.
   Never restore over the failed/source database or bucket.
3. Restore `database.dump` with the provider's PostgreSQL-compatible
   `pg_restore --exit-on-error --no-owner --no-privileges` workflow.
4. Restore every object key and byte from `objects/` with provider-supported
   S3/MinIO tooling.
5. Repeat the drill validations: migration, counts, constraints, full object
   inventory, metadata-to-object mapping, sizes, hashes, and opening at least
   one document.
6. Configure a non-public ENMA deployment to use the restored stores and run
   application smoke tests. Promote traffic only after validation and incident
   approval. Keep the old environment isolated; do not delete it as part of
   restore.

On any failure, stop. Preserve the report and logs in the restricted incident
location, correct the dependency or choose another valid backup, and repeat in
a new empty target. Do not alter restored data to make validation pass.

## Production provider contract

Before go-live, the selected PostgreSQL service must provide automatic daily
backups, retention meeting this policy, restore to a separate instance,
encryption at rest, restricted access, and backup-failure monitoring. The
object service must provide a private encrypted bucket, separate least-privilege
credentials, compatible retention, isolated restore, and versioning or an
equivalent backup/snapshot facility.

Point-in-time recovery, cross-region replication, and immutable/WORM copies are
optional for Closed Beta unless later risk analysis makes them mandatory.
Provider setup, schedules, alerts, encryption, lifecycle rules, and an actual
provider restore must be verified in the Production Readiness Gate. Until then,
production backup activation remains pending.
