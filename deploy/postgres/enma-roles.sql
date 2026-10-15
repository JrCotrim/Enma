-- ENMA PostgreSQL roles and grants (D33-D35, D37).
--
-- The single source of the database identities: Production
-- (scripts/production/common.sh), the Pilot, Development and the restore drill
-- (scripts/pilot/Pilot.Common.ps1) and the test fixtures all run this file
-- unchanged, as the bootstrap superuser (over the container's local socket in
-- the scripts). It is never a migration and never an initdb.d script.
--
--   migrator  owns the database and every object in schema public, applies
--             the EF Core migrations and restores dumps;
--   app       the API: CONNECT, USAGE on public and SELECT, INSERT, UPDATE,
--             DELETE on every table, except audit_logs (SELECT, INSERT) and
--             "__EFMigrationsHistory" (SELECT). No TRUNCATE, REFERENCES,
--             TRIGGER, MAINTAIN, CREATE or TEMP.
--
-- The caller sends this file, then exactly one call:
--
--   SELECT pg_temp.enma_apply_roles('<migrator>', '<app>', '<migrator-password>', '<app-password>');
--     Before every migration and restore: creates the roles if missing and
--     (re)sets their attributes and passwords, gives the database to the
--     migrator, revokes CONNECT and TEMP from PUBLIC, grants CONNECT to both
--     roles and USAGE on public to the app, and converts the existing objects
--     to the migrator (a database created before the roles cannot be migrated
--     by the migrator otherwise).
--
--   SELECT pg_temp.enma_apply_grants('<migrator>', '<app>');
--     After every migration and restore: converts every object in public to
--     the migrator, object by object, resets the table privileges to the map
--     above and the migrator's default privileges for future tables, then
--     verifies the result.
--
-- Both are idempotent and transactional, and fail closed: an invalid,
-- reserved, superuser or member role, a schema other than public, or an
-- object kind not listed in enma_assert_inventory aborts with nothing
-- changed. A new kind is a deliberate review, not a silent grant.
--
-- Passwords appear only in the final call, never in this file. The statements
-- below keep the session out of the server log before the call arrives, and a
-- failed password change is re-raised without its statement. Role passwords
-- are SCRAM-hashed by the server; each run re-salts them.

SET log_statement = 'none';
SET log_min_error_statement = 'panic';
SET log_min_messages = 'panic';
SET log_min_duration_statement = -1;
SET log_min_duration_sample = -1;
SET log_transaction_sample_rate = 0;
SET log_parameter_max_length = 0;
SET log_parameter_max_length_on_error = 0;
SET search_path = pg_catalog;
SET lock_timeout = '15s';

CREATE OR REPLACE FUNCTION pg_temp.enma_assert_identities(
    migrator_role text, app_role text, require_existing boolean)
RETURNS void
LANGUAGE plpgsql
AS $function$
DECLARE
    found text;
BEGIN
    IF NOT coalesce((SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user), false) THEN
        RAISE EXCEPTION 'enma-roles.sql must run as the bootstrap superuser';
    END IF;
    IF migrator_role IS NULL OR migrator_role !~ '^[a-z_][a-z0-9_]{0,62}$' OR migrator_role ~ '^pg_' THEN
        RAISE EXCEPTION 'invalid migrator role name (expected ^[a-z_][a-z0-9_]{0,62}$, not pg_*)';
    END IF;
    IF app_role IS NULL OR app_role !~ '^[a-z_][a-z0-9_]{0,62}$' OR app_role ~ '^pg_' THEN
        RAISE EXCEPTION 'invalid application role name (expected ^[a-z_][a-z0-9_]{0,62}$, not pg_*)';
    END IF;
    IF migrator_role = app_role THEN
        RAISE EXCEPTION 'the migrator and application roles must differ';
    END IF;
    IF current_user IN (migrator_role, app_role) THEN
        RAISE EXCEPTION 'the bootstrap superuser cannot be the migrator or application role';
    END IF;

    SELECT string_agg(rolname, ', ' ORDER BY rolname) INTO found
    FROM pg_catalog.pg_roles
    WHERE rolname IN (migrator_role, app_role) AND rolsuper;
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'role % is a superuser; refusing to use it', found;
    END IF;

    SELECT string_agg(format('%s in %s', pg_catalog.pg_get_userbyid(m.member),
                             pg_catalog.pg_get_userbyid(m.roleid)), ', ') INTO found
    FROM pg_catalog.pg_auth_members m
    WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_roles r
                  WHERE r.rolname IN (migrator_role, app_role)
                    AND r.oid IN (m.member, m.roleid));
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'unexpected role membership: %', found;
    END IF;

    IF require_existing THEN
        SELECT string_agg(rolname, ', ' ORDER BY rolname) INTO found
        FROM pg_catalog.pg_roles
        WHERE rolname IN (migrator_role, app_role)
          AND (NOT rolcanlogin OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls);
        IF found IS NOT NULL THEN
            RAISE EXCEPTION 'role % has unexpected attributes; run enma_apply_roles first', found;
        END IF;
        IF (SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname IN (migrator_role, app_role)) <> 2 THEN
            RAISE EXCEPTION 'the migrator and application roles must exist; run enma_apply_roles first';
        END IF;
    END IF;
END;
$function$;

-- Every object outside the system schemas must be one of the kinds the
-- grants below were designed for: tables and their indexes, row and array
-- types, SECURITY INVOKER functions, and the migrator's default privileges.
CREATE OR REPLACE FUNCTION pg_temp.enma_assert_inventory(migrator_role text)
RETURNS void
LANGUAGE plpgsql
AS $function$
DECLARE
    public_oid oid := 'public'::pg_catalog.regnamespace;
    found text;
BEGIN
    SELECT string_agg(nspname, ', ' ORDER BY nspname) INTO found
    FROM pg_catalog.pg_namespace
    WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'public')
      AND nspname !~ '^pg_(temp|toast_temp)_[0-9]+$';
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'unexpected schema outside public: %', found;
    END IF;

    IF (SELECT pg_catalog.pg_get_userbyid(nspowner) FROM pg_catalog.pg_namespace WHERE oid = public_oid)
        NOT IN ('pg_database_owner', migrator_role) THEN
        RAISE EXCEPTION 'schema public must be owned by pg_database_owner or the migrator';
    END IF;

    SELECT string_agg(format('%s (relkind %s)', relname, relkind), ', ' ORDER BY relname) INTO found
    FROM pg_catalog.pg_class
    WHERE relnamespace = public_oid AND relkind NOT IN ('r', 'i');
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'unexpected relation kind in public: %', found;
    END IF;

    SELECT string_agg(oid::pg_catalog.regprocedure::text, ', ' ORDER BY oid) INTO found
    FROM pg_catalog.pg_proc
    WHERE pronamespace = public_oid AND (prokind <> 'f' OR prosecdef);
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'unexpected routine in public (only SECURITY INVOKER functions): %', found;
    END IF;

    SELECT string_agg(t.typname, ', ' ORDER BY t.typname) INTO found
    FROM pg_catalog.pg_type t
    WHERE t.typnamespace = public_oid
      AND NOT EXISTS (
          SELECT 1 FROM pg_catalog.pg_class c
          WHERE c.oid = t.typrelid AND c.relkind = 'r')
      AND NOT EXISTS (
          SELECT 1 FROM pg_catalog.pg_type e
          JOIN pg_catalog.pg_class c ON c.oid = e.typrelid
          WHERE e.oid = t.typelem AND e.typarray = t.oid AND c.relkind = 'r');
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'unexpected type in public: %', found;
    END IF;

    SELECT string_agg(kind || ' ' || name, ', ' ORDER BY kind, name) INTO found
    FROM (
        SELECT 'collation' AS kind, collname::text AS name FROM pg_catalog.pg_collation WHERE collnamespace = public_oid
        UNION ALL SELECT 'conversion', conname::text FROM pg_catalog.pg_conversion WHERE connamespace = public_oid
        UNION ALL SELECT 'operator', oprname::text FROM pg_catalog.pg_operator WHERE oprnamespace = public_oid
        UNION ALL SELECT 'operator class', opcname::text FROM pg_catalog.pg_opclass WHERE opcnamespace = public_oid
        UNION ALL SELECT 'operator family', opfname::text FROM pg_catalog.pg_opfamily WHERE opfnamespace = public_oid
        UNION ALL SELECT 'text search configuration', cfgname::text FROM pg_catalog.pg_ts_config WHERE cfgnamespace = public_oid
        UNION ALL SELECT 'text search dictionary', dictname::text FROM pg_catalog.pg_ts_dict WHERE dictnamespace = public_oid
        UNION ALL SELECT 'text search parser', prsname::text FROM pg_catalog.pg_ts_parser WHERE prsnamespace = public_oid
        UNION ALL SELECT 'text search template', tmplname::text FROM pg_catalog.pg_ts_template WHERE tmplnamespace = public_oid
        UNION ALL SELECT 'statistics object', stxname::text FROM pg_catalog.pg_statistic_ext
        UNION ALL SELECT 'extension', extname::text FROM pg_catalog.pg_extension WHERE extname <> 'plpgsql'
        UNION ALL SELECT 'language', lanname::text FROM pg_catalog.pg_language WHERE lanname NOT IN ('internal', 'c', 'sql', 'plpgsql')
        UNION ALL SELECT 'event trigger', evtname::text FROM pg_catalog.pg_event_trigger
        UNION ALL SELECT 'publication', pubname::text FROM pg_catalog.pg_publication
        UNION ALL SELECT 'foreign data wrapper', fdwname::text FROM pg_catalog.pg_foreign_data_wrapper
        UNION ALL SELECT 'foreign server', srvname::text FROM pg_catalog.pg_foreign_server
        UNION ALL SELECT 'large object', oid::text FROM pg_catalog.pg_largeobject_metadata
        UNION ALL SELECT 'policy', polname::text FROM pg_catalog.pg_policy
        UNION ALL SELECT 'rule', r.rulename::text
            FROM pg_catalog.pg_rewrite r JOIN pg_catalog.pg_class c ON c.oid = r.ev_class
            WHERE c.relnamespace = public_oid
        UNION ALL SELECT 'default privileges',
                format('for %s in %s on %s', pg_catalog.pg_get_userbyid(defaclrole),
                       CASE defaclnamespace WHEN 0 THEN '*' ELSE defaclnamespace::pg_catalog.regnamespace::text END,
                       defaclobjtype)
            FROM pg_catalog.pg_default_acl
            WHERE NOT (pg_catalog.pg_get_userbyid(defaclrole) = migrator_role
                       AND defaclnamespace = public_oid AND defaclobjtype = 'r')
    ) unexpected;
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'unexpected database object: %', found;
    END IF;
END;
$function$;

-- Tables carry their indexes, row and array types along; functions are
-- converted one by one. Anything left with another owner aborts.
CREATE OR REPLACE FUNCTION pg_temp.enma_convert_ownership(migrator_role text)
RETURNS void
LANGUAGE plpgsql
AS $function$
DECLARE
    public_oid oid := 'public'::pg_catalog.regnamespace;
    migrator_oid oid := migrator_role::pg_catalog.regrole;
    item record;
    found text;
BEGIN
    PERFORM pg_temp.enma_assert_inventory(migrator_role);

    FOR item IN
        SELECT relname FROM pg_catalog.pg_class
        WHERE relnamespace = public_oid AND relkind = 'r' AND relowner <> migrator_oid
        ORDER BY relname
    LOOP
        EXECUTE format('ALTER TABLE public.%I OWNER TO %I', item.relname, migrator_role);
    END LOOP;

    FOR item IN
        SELECT oid::pg_catalog.regprocedure AS signature FROM pg_catalog.pg_proc
        WHERE pronamespace = public_oid AND proowner <> migrator_oid
        ORDER BY oid
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO %I', item.signature, migrator_role);
    END LOOP;

    SELECT string_agg(name, ', ' ORDER BY name) INTO found
    FROM (
        SELECT relname::text AS name FROM pg_catalog.pg_class
        WHERE relnamespace = public_oid AND relowner <> migrator_oid
        UNION ALL SELECT oid::pg_catalog.regprocedure::text FROM pg_catalog.pg_proc
        WHERE pronamespace = public_oid AND proowner <> migrator_oid
        UNION ALL SELECT typname::text FROM pg_catalog.pg_type
        WHERE typnamespace = public_oid AND typowner <> migrator_oid
    ) unconverted;
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'objects in public not owned by the migrator after conversion: %', found;
    END IF;
END;
$function$;

-- Attributes and password in one statement: a failure there is re-raised
-- without the statement text, which holds the password.
CREATE OR REPLACE FUNCTION pg_temp.enma_set_login(role_name text, role_password text, label text)
RETURNS void
LANGUAGE plpgsql
AS $function$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = role_name) THEN
        EXECUTE format('CREATE ROLE %I', role_name);
    END IF;
    BEGIN
        EXECUTE format(
            'ALTER ROLE %I WITH LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE '
            'NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1 VALID UNTIL %L PASSWORD %L',
            role_name, 'infinity', role_password);
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'could not set the % role login (SQLSTATE %)', label, SQLSTATE
            USING ERRCODE = SQLSTATE;
    END;
END;
$function$;

CREATE OR REPLACE FUNCTION pg_temp.enma_apply_roles(
    migrator_role text, app_role text, migrator_password text, app_password text)
RETURNS void
LANGUAGE plpgsql
AS $function$
DECLARE
    database_name text := current_database();
BEGIN
    PERFORM pg_temp.enma_assert_identities(migrator_role, app_role, false);
    IF coalesce(length(migrator_password), 0) < 16 OR coalesce(length(app_password), 0) < 16 THEN
        RAISE EXCEPTION 'role passwords must have at least 16 characters';
    END IF;
    IF migrator_password = app_password THEN
        RAISE EXCEPTION 'the migrator and application passwords must differ';
    END IF;

    PERFORM pg_temp.enma_set_login(migrator_role, migrator_password, 'migrator');
    PERFORM pg_temp.enma_set_login(app_role, app_password, 'application');

    EXECUTE format('ALTER DATABASE %I OWNER TO %I', database_name, migrator_role);
    EXECUTE format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', database_name);
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I, %I', database_name, migrator_role, app_role);
    EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', app_role);

    PERFORM pg_temp.enma_convert_ownership(migrator_role);
END;
$function$;

CREATE OR REPLACE FUNCTION pg_temp.enma_assert_privileges(migrator_role text, app_role text)
RETURNS void
LANGUAGE plpgsql
AS $function$
DECLARE
    public_oid oid := 'public'::pg_catalog.regnamespace;
    migrator_oid oid := migrator_role::pg_catalog.regrole;
    app_oid oid := app_role::pg_catalog.regrole;
    database_name text := current_database();
    found text;
BEGIN
    SELECT string_agg(format('%s has {%s}, expected {%s}', relname, actual, expected), '; ' ORDER BY relname)
    INTO found
    FROM (
        SELECT c.relname,
               CASE c.relname
                   WHEN 'audit_logs' THEN 'SELECT,INSERT'
                   WHEN '__EFMigrationsHistory' THEN 'SELECT'
                   ELSE 'SELECT,INSERT,UPDATE,DELETE'
               END AS expected,
               concat_ws(',',
                   CASE WHEN has_table_privilege(app_oid, c.oid, 'SELECT') THEN 'SELECT' END,
                   CASE WHEN has_table_privilege(app_oid, c.oid, 'INSERT') THEN 'INSERT' END,
                   CASE WHEN has_table_privilege(app_oid, c.oid, 'UPDATE') THEN 'UPDATE' END,
                   CASE WHEN has_table_privilege(app_oid, c.oid, 'DELETE') THEN 'DELETE' END,
                   CASE WHEN has_table_privilege(app_oid, c.oid, 'TRUNCATE') THEN 'TRUNCATE' END,
                   CASE WHEN has_table_privilege(app_oid, c.oid, 'REFERENCES') THEN 'REFERENCES' END,
                   CASE WHEN has_table_privilege(app_oid, c.oid, 'TRIGGER') THEN 'TRIGGER' END,
                   CASE WHEN has_table_privilege(app_oid, c.oid, 'MAINTAIN') THEN 'MAINTAIN' END)
                   AS actual
        FROM pg_catalog.pg_class c
        WHERE c.relnamespace = public_oid AND c.relkind = 'r'
    ) privileges
    WHERE actual <> expected;
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'unexpected application table privileges: %', found;
    END IF;

    SELECT string_agg(DISTINCT c.relname, ', ') INTO found
    FROM pg_catalog.pg_class c
    LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attacl IS NOT NULL
    WHERE c.relnamespace = public_oid
      AND (a.attrelid IS NOT NULL
           OR EXISTS (SELECT 1 FROM pg_catalog.aclexplode(c.relacl) x
                      WHERE x.grantee NOT IN (migrator_oid, app_oid)
                         OR x.grantor <> migrator_oid));
    IF found IS NOT NULL THEN
        RAISE EXCEPTION 'unexpected grantee, grantor or column privilege on: %', found;
    END IF;

    IF NOT has_database_privilege(app_oid, database_name, 'CONNECT')
       OR has_database_privilege(app_oid, database_name, 'TEMPORARY')
       OR has_database_privilege(app_oid, database_name, 'CREATE')
       OR NOT has_database_privilege(migrator_oid, database_name, 'CONNECT')
       OR (SELECT datacl IS NULL OR datdba <> migrator_oid
           FROM pg_catalog.pg_database WHERE datname = database_name)
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_database d, pg_catalog.aclexplode(d.datacl) x
                  WHERE d.datname = database_name AND x.grantee = 0) THEN
        RAISE EXCEPTION 'unexpected database privileges';
    END IF;

    IF NOT has_schema_privilege(app_oid, public_oid, 'USAGE')
       OR has_schema_privilege(app_oid, public_oid, 'CREATE')
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n, pg_catalog.aclexplode(n.nspacl) x
                  WHERE n.oid = public_oid AND x.grantee = 0 AND x.privilege_type = 'CREATE') THEN
        RAISE EXCEPTION 'unexpected privileges on schema public';
    END IF;

    IF (SELECT string_agg(format('%s:%s', pg_catalog.pg_get_userbyid(x.grantee), x.privilege_type),
                          ',' ORDER BY x.privilege_type)
        FROM pg_catalog.pg_default_acl d, pg_catalog.aclexplode(d.defaclacl) x
        WHERE d.defaclrole = migrator_oid AND d.defaclnamespace = public_oid AND d.defaclobjtype = 'r')
       IS DISTINCT FROM format('%1$s:DELETE,%1$s:INSERT,%1$s:SELECT,%1$s:UPDATE', app_role) THEN
        RAISE EXCEPTION 'unexpected default privileges for future tables';
    END IF;
END;
$function$;

CREATE OR REPLACE FUNCTION pg_temp.enma_apply_grants(migrator_role text, app_role text)
RETURNS void
LANGUAGE plpgsql
AS $function$
DECLARE
    public_oid oid := 'public'::pg_catalog.regnamespace;
    migrator_oid oid;
    item record;
    grantee_name text;
BEGIN
    PERFORM pg_temp.enma_assert_identities(migrator_role, app_role, true);
    migrator_oid := migrator_role::pg_catalog.regrole;
    IF (SELECT datdba FROM pg_catalog.pg_database WHERE datname = current_database()) <> migrator_oid THEN
        RAISE EXCEPTION 'the database is not owned by the migrator; run enma_apply_roles first';
    END IF;

    PERFORM pg_temp.enma_convert_ownership(migrator_role);
    IF to_regclass('public.audit_logs') IS NULL OR to_regclass('public."__EFMigrationsHistory"') IS NULL THEN
        RAISE EXCEPTION 'audit_logs and "__EFMigrationsHistory" must exist; apply the migrations first';
    END IF;

    EXECUTE 'REVOKE CREATE ON SCHEMA public FROM PUBLIC';
    EXECUTE format('REVOKE ALL ON SCHEMA public FROM %I', app_role);
    EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', app_role);

    -- Every grantee except the owner loses everything on every table
    -- (column privileges included), then the app gets the DML map.
    FOR item IN
        SELECT oid, relname FROM pg_catalog.pg_class
        WHERE relnamespace = public_oid AND relkind = 'r'
        ORDER BY relname
    LOOP
        FOR grantee_name IN
            SELECT DISTINCT CASE WHEN grantee = 0 THEN 'PUBLIC'
                                 ELSE quote_ident(pg_catalog.pg_get_userbyid(grantee)) END
            FROM (
                SELECT (pg_catalog.aclexplode(relacl)).grantee FROM pg_catalog.pg_class
                WHERE oid = item.oid
                UNION ALL
                SELECT (pg_catalog.aclexplode(attacl)).grantee FROM pg_catalog.pg_attribute
                WHERE attrelid = item.oid AND attacl IS NOT NULL
            ) grantees
            WHERE grantee <> migrator_oid
        LOOP
            EXECUTE format('REVOKE ALL ON TABLE public.%I FROM %s CASCADE', item.relname, grantee_name);
        END LOOP;
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO %I', item.relname, app_role);
    END LOOP;

    -- D33 exceptions: the audit trail is append-only and the migration
    -- history is read-only for the API.
    EXECUTE format('REVOKE UPDATE, DELETE ON TABLE public.audit_logs FROM %I', app_role);
    EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON TABLE public.%I FROM %I', '__EFMigrationsHistory', app_role);

    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON TABLES FROM %I',
                   migrator_role, app_role);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public '
                   'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', migrator_role, app_role);

    PERFORM pg_temp.enma_assert_privileges(migrator_role, app_role);
END;
$function$;
