-- =============================================================================
-- setup-least-privilege-db.sql
-- Hospital_CRM Database Security Hardening Script
-- Implements Task 0.2: Enforce Database-Level Least Privilege (app_user)
-- =============================================================================

\set ON_ERROR_STOP on
-- Privilege-only operation. Provision the login/credential separately via the secret manager.
BEGIN;
-- 1. Refuse a missing or privileged runtime role; never install a default password.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_user' AND rolcanlogin
        AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication) THEN
        RAISE EXCEPTION 'Provision a non-privileged app_user login before applying runtime grants';
    END IF;
END $$;

-- 2. Grant schema usage and connect privileges
SELECT format('GRANT CONNECT ON DATABASE %I TO app_user', current_database()) \gexec
REVOKE CREATE ON SCHEMA public FROM PUBLIC, app_user;
GRANT USAGE ON SCHEMA public TO app_user;

-- 3. Grant basic CRUD on all application tables
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_user;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;

-- 4. Automatically apply to future tables created in public schema
--    Every new table inherits UPDATE/DELETE here, including new audit tables. The
--    `audit_append_only` DDL trigger created by migration EnforceAuditAppendOnly
--    revokes them again for the audit tables below, so the list must be kept in both
--    places when an audit table is added.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO app_user;

-- 5. Strict Append-Only Enforcement on Audit Tables (REVOKE UPDATE & DELETE)
-- App user can insert new audit records and select for querying, but can NEVER update or delete.
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "PatientAuditLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "NurseAuditLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "NotificationLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "PriorityLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "ImpersonationLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "ImpersonationPhiAccessLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "AppointmentHistories" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "ControlledSubstanceRegisters" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "AuthAuditLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "DrugStockMovements" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "StockMovements" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "MedicineSearchMissLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "PrescriptionPrintLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "FinanceLedgerEntries" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "InvoicePrintLogs" FROM app_user;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "CreditNotes" FROM app_user;

GRANT INSERT, SELECT ON TABLE "PatientAuditLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "NurseAuditLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "NotificationLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "PriorityLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "ImpersonationLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "ImpersonationPhiAccessLogs" TO app_user;
GRANT UPDATE ("EndedAt") ON TABLE "ImpersonationLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "AppointmentHistories" TO app_user;
GRANT INSERT, SELECT ON TABLE "ControlledSubstanceRegisters" TO app_user;
GRANT INSERT, SELECT ON TABLE "AuthAuditLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "DrugStockMovements" TO app_user;
GRANT INSERT, SELECT ON TABLE "StockMovements" TO app_user;
GRANT INSERT, SELECT ON TABLE "MedicineSearchMissLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "PrescriptionPrintLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "FinanceLedgerEntries" TO app_user;
GRANT INSERT, SELECT ON TABLE "InvoicePrintLogs" TO app_user;
GRANT INSERT, SELECT ON TABLE "CreditNotes" TO app_user;
-- Dual-control approval is the one legitimate credit-note state change.
GRANT UPDATE ("Status", "SecondApprovedByUserId", "IssuedAt") ON TABLE "CreditNotes" TO app_user;

-- Booking intent is immutable except for its durable handoff acknowledgement.
DO $$
BEGIN
    IF to_regclass('public."NotificationIntents"') IS NOT NULL THEN
        REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "NotificationIntents" FROM app_user;
        GRANT SELECT, INSERT ON TABLE "NotificationIntents" TO app_user;
        GRANT UPDATE ("HandedOffAt") ON TABLE "NotificationIntents" TO app_user;
    END IF;
END $$;

-- Provisioning keys survive invitation/tenant removal; only the saved send outcome changes.
DO $$
BEGIN
    IF to_regclass('public."TenantProvisioningReceipts"') IS NOT NULL THEN
        REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "TenantProvisioningReceipts" FROM app_user;
        GRANT SELECT, INSERT ON TABLE "TenantProvisioningReceipts" TO app_user;
        GRANT UPDATE ("ProtectedResponse") ON TABLE "TenantProvisioningReceipts" TO app_user;
    END IF;
END $$;

-- Session revocation is a separately approved security operation, never part of grants recovery.

-- 7. Hangfire background jobs live in their own schema, which the public-schema
--    grants above do not reach. Without this the API cannot start: Hangfire fails
--    to acquire its distributed locks and aborts with a PostgreSqlDistributedLockException.
--    Prepare Hangfire tables with the schema owner before runtime starts.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_namespace WHERE nspname = 'hangfire') THEN
        EXECUTE 'CREATE SCHEMA hangfire';
    END IF;
END $$;

GRANT USAGE ON SCHEMA hangfire TO app_user;
REVOKE CREATE ON SCHEMA hangfire FROM PUBLIC, app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA hangfire TO app_user;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA hangfire TO app_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA hangfire GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA hangfire GRANT USAGE, SELECT ON SEQUENCES TO app_user;
COMMIT;

