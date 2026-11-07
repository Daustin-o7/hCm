\set ON_ERROR_STOP on
-- Run as the actual API/Hangfire runtime login, not the owner. Read-only assertions.
BEGIN READ ONLY;
DO $$
BEGIN
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = current_user AND
        (rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication)) OR
       EXISTS (SELECT FROM pg_roles r WHERE (r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication) AND pg_has_role(current_user, r.oid, 'MEMBER')) OR
       EXISTS (SELECT FROM pg_database WHERE datname = current_database() AND pg_has_role(current_user, datdba, 'MEMBER')) OR
       EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname IN ('public', 'hangfire') AND pg_has_role(current_user, c.relowner, 'MEMBER')) THEN
        RAISE EXCEPTION 'Runtime must not inherit privileged roles or database/table ownership';
    END IF;
    IF has_schema_privilege(current_user, 'public', 'CREATE') OR
       has_schema_privilege(current_user, 'hangfire', 'CREATE') THEN
        RAISE EXCEPTION 'Runtime schema DDL is not allowed';
    END IF;
    IF EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relname IN
            ('PatientAuditLogs','NurseAuditLogs','NotificationLogs','PriorityLogs','ImpersonationLogs','ImpersonationPhiAccessLogs',
             'AppointmentHistories','ControlledSubstanceRegisters','AuthAuditLogs','DrugStockMovements','StockMovements',
              'MedicineSearchMissLogs','PrescriptionPrintLogs','FinanceLedgerEntries','InvoicePrintLogs','CreditNotes','NotificationIntents','TenantProvisioningReceipts')
        AND (has_table_privilege(current_user, c.oid, 'UPDATE') OR has_table_privilege(current_user, c.oid, 'DELETE') OR has_table_privilege(current_user, c.oid, 'TRUNCATE'))) THEN
        RAISE EXCEPTION 'Runtime has mutable access to protected history';
    END IF;
    IF EXISTS (SELECT FROM (VALUES ('Patients'), ('Appointments'), ('Consultations'), ('Invoices'), ('Payments'), ('NotificationIntents'), ('TenantProvisioningReceipts')) required(name)
        LEFT JOIN pg_class c ON c.relname = required.name AND c.relnamespace = 'public'::regnamespace
        WHERE c.oid IS NULL OR NOT c.relrowsecurity) THEN
        RAISE EXCEPTION 'Required tenant RLS tables are missing or disabled';
    END IF;
END $$;
ROLLBACK;
