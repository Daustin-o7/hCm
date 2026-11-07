-- Run with an authorized schema-owner connection BEFORE the sale-readiness migration.
-- Results identify blockers; this script never repairs or removes business records.
\set ON_ERROR_STOP on
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '60s';
-- Fail rather than silently returning a tenant-filtered subset under app_user.
SET LOCAL row_security = off;
SELECT current_database() AS database, current_user AS reviewer_role, now() AS inspected_at;

WITH keys AS (
    SELECT 'Patients' AS source, "TenantId", "IdempotencyKey", "Id" FROM "Patients"
    UNION ALL SELECT 'Invoices', "TenantId", "IdempotencyKey", "Id" FROM "Invoices"
    UNION ALL SELECT 'Payments', "TenantId", "IdempotencyKey", "Id" FROM "Payments"
    UNION ALL SELECT 'PatientDeposits', "TenantId", "IdempotencyKey", "Id" FROM "PatientDeposits"
    UNION ALL SELECT 'DispenseRecords', "TenantId", "IdempotencyKey", "Id" FROM "DispenseRecords"
)
SELECT 'duplicate_tenant_retry_key' AS finding, source, "TenantId", array_agg("Id" ORDER BY "Id") AS record_ids
FROM keys WHERE "IdempotencyKey" IS NOT NULL
GROUP BY source, "TenantId", "IdempotencyKey" HAVING count(*) > 1;

-- PaymentMethod.Razorpay = 5; negative refund rows deliberately share capture IDs.
SELECT 'duplicate_positive_razorpay_capture' AS finding, "TenantId", array_agg("Id" ORDER BY "Id") AS payment_ids
FROM "Payments" WHERE "Method" = 5 AND "Amount" > 0 AND "RazorpayPaymentId" IS NOT NULL
GROUP BY "TenantId", "Method", "RazorpayPaymentId" HAVING count(*) > 1;

SELECT 'duplicate_tenant_webhook_event' AS finding, "TenantId", "Provider", array_agg("Id" ORDER BY "Id") AS event_ids
FROM "ProcessedWebhookEvents" GROUP BY "TenantId", "Provider", "EventId" HAVING count(*) > 1;

-- Ambiguous orders cannot be routed by the anonymous, signature-verified legacy webhook.
SELECT 'ambiguous_provider_order' AS finding, array_agg("Id" ORDER BY "Id") AS payment_ids
FROM "Payments" WHERE NULLIF("RazorpayOrderId", '') IS NOT NULL
GROUP BY "RazorpayOrderId" HAVING count(*) > 1;

SELECT 'empty_tenant_owner' AS finding, source, count(*) AS affected_rows FROM (
    SELECT 'Patients' AS source, "TenantId" FROM "Patients"
    UNION ALL SELECT 'Invoices', "TenantId" FROM "Invoices"
    UNION ALL SELECT 'Payments', "TenantId" FROM "Payments"
    UNION ALL SELECT 'Appointments', "TenantId" FROM "Appointments"
    UNION ALL SELECT 'Consultations', "TenantId" FROM "Consultations"
) scoped WHERE "TenantId" = '00000000-0000-0000-0000-000000000000'::uuid GROUP BY source;
ROLLBACK;
