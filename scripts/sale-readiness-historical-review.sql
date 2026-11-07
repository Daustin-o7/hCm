-- Read-only review AFTER the additive sale-readiness migration. Requires an authorized
-- schema-owner connection. Findings need clinician/pharmacist/accountant adjudication;
-- none authorizes a backfill, historical re-signing, deletion, payment or stock movement.
\set ON_ERROR_STOP on
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '60s';
SET LOCAL row_security = off;
SELECT current_database() AS database, current_user AS reviewer_role, now() AS inspected_at;

SELECT 'legacy_or_missing_clinical_seal' AS finding, "TenantId", "Id", "AppointmentId", "Version"
FROM "Consultations" WHERE "Status" IN (1, 2) AND COALESCE("DigitalSignatureHash", '') NOT LIKE 'v2:%';
-- SQL does not recompute the v2 seal. Verify it through the application's bound verification path.
SELECT 'invalid_fulfillment_quantity' AS finding, c."TenantId", c."Id" AS consultation_id, i."Id" AS item_id
FROM "PrescriptionItems" i JOIN "Prescriptions" p ON p."Id" = i."PrescriptionId"
JOIN "Consultations" c ON c."Id" = p."ConsultationId"
WHERE i."DispensedQuantity" < 0 OR i."DispensedQuantity" > i."TotalQuantity"
    OR (i."IsDispensed" AND i."DispensedQuantity" < i."TotalQuantity");

WITH collections AS (
    SELECT "TenantId", "InvoiceId", sum("Amount") AS net_confirmed
    FROM "Payments" WHERE "VerificationStatus" = 2
        AND (("Amount" > 0 AND "Status" = 1) OR ("Amount" < 0 AND "Status" = 3))
    GROUP BY "TenantId", "InvoiceId"
), credits AS (
    SELECT "TenantId", "InvoiceId", sum("Amount") AS approved
    FROM "CreditNotes" WHERE "Status" = 1 GROUP BY "TenantId", "InvoiceId"
)
SELECT 'invoice_collection_or_credit_mismatch' AS finding, i."TenantId", i."Id",
    i."PaidAmount", COALESCE(p.net_confirmed, 0) AS confirmed_net,
    i."CreditedAmount", COALESCE(c.approved, 0) AS approved_credits, i."BalanceDue"
FROM "Invoices" i LEFT JOIN collections p ON (p."TenantId", p."InvoiceId") = (i."TenantId", i."Id")
LEFT JOIN credits c ON (c."TenantId", c."InvoiceId") = (i."TenantId", i."Id")
WHERE i."PaidAmount" <> COALESCE(p.net_confirmed, 0) OR i."CreditedAmount" <> COALESCE(c.approved, 0)
    OR i."BalanceDue" <> greatest(0, i."Total" - i."PaidAmount" - i."CreditedAmount");

-- Persisted InvoiceType selects the current format: pharmacy UnitPrice is MRP-inclusive
-- and Amount excludes tax; consultation UnitPrice excludes tax and Amount includes it.
-- No pricing-version flag exists: other legacy formats need accountant adjudication.
SELECT 'invoice_line_or_tax_mismatch' AS finding, i."TenantId", i."Id"
FROM "Invoices" i LEFT JOIN "InvoiceLineItems" l ON l."InvoiceId" = i."Id"
GROUP BY i."Id" HAVING count(l."Id") = 0 OR i."GstAmount" <> COALESCE(sum(l."TaxAmount"), 0)
    OR i."Total" <> COALESCE(sum(l."Amount" + CASE WHEN i."InvoiceType" = 1 THEN l."TaxAmount" ELSE 0 END), 0) - i."DiscountAmount"
    OR bool_or(l."Amount" <> CASE WHEN i."InvoiceType" = 1 THEN l."Quantity" * l."UnitPrice" - l."TaxAmount"
        ELSE l."Quantity" * l."UnitPrice" - l."DiscountAmount" + l."TaxAmount" END);

-- Posting pairs can span different reference IDs; use tenant/day totals as a lead,
-- then reconcile the saved payment/invoice/note chain. A zero total proves no lineage.
SELECT 'unbalanced_ledger_day' AS finding, "TenantId", ("CreatedAt" AT TIME ZONE 'Asia/Kolkata')::date AS local_date,
    sum("DebitAmount") AS debit, sum("CreditAmount") AS credit
FROM "FinanceLedgerEntries" GROUP BY "TenantId", ("CreatedAt" AT TIME ZONE 'Asia/Kolkata')::date
HAVING sum("DebitAmount") <> sum("CreditAmount");

SELECT 'refund_needs_provider_or_manual_evidence' AS finding, "TenantId", "Id", "InvoiceId", "Status"
FROM "Payments" WHERE "Amount" < 0 AND ("Status" = 0
    OR ("Status" = 3 AND (NULLIF("RefundReference", '') IS NULL OR "PaidAt" IS NULL
        OR ("Method" = 5 AND "RefundSourcePaymentId" IS NULL))));

SELECT 'historical_deposit_allocation_without_payment_lineage' AS finding, "TenantId", "Id", "InvoiceId"
FROM "PatientDepositAllocations" WHERE "PaymentId" IS NULL;
SELECT 'historical_supply_without_retry_evidence' AS finding, "TenantId", "Id", "InvoiceId"
FROM "DispenseRecords" WHERE "InvoiceId" IS NULL OR "RequestHash" IS NULL OR "ResultJson" IS NULL;
SELECT 'historical_checkout_without_retry_evidence' AS finding, "TenantId", "Id"
FROM "Invoices" WHERE "InvoiceType" = 1 AND ("CheckoutRequestHash" IS NULL OR "CheckoutResultJson" IS NULL)
    AND NOT EXISTS (SELECT 1 FROM "DispenseRecords" d WHERE d."TenantId" = "Invoices"."TenantId" AND d."InvoiceId" = "Invoices"."Id");

-- Part 1 checklist follow-up. Review owners (including the queries above):
-- treating clinician: authorship, credentials, seals; clinician + pharmacist: fulfillment;
-- finance lead/accountant: collections, lines, journals, deposits and refund provider/origin;
-- pharmacist + finance: supply, checkout and return stock; clinic administrator: registration.
-- Findings are candidates, not proof of misconduct. Clinical correction requires a new
-- reasoned version; finance correction requires approved compensating postings/provider
-- reconciliation. Obtain approval, backups and a disposable rehearsal before any correction.

-- Treating-doctor assignment is current state; reassignment history needs human review.
SELECT 'foreign_authored_draft' AS finding, c."TenantId", c."Id", c."AppointmentId",
    c."DoctorId" AS author, a."DoctorId" AS treating_doctor
FROM "Consultations" c LEFT JOIN "Appointments" a
    ON (a."TenantId", a."Id") = (c."TenantId", c."AppointmentId")
WHERE c."Status" = 0 AND (a."Id" IS NULL OR c."DoctorId" IS DISTINCT FROM a."DoctorId");

-- Only the exact placeholder rejected by ConsultationsController is recognized.
-- Ordinary qualifications (e.g. MBBS) or unfamiliar registration formats imply nothing.
-- Saved signer snapshots, not today's membership, are the historical evidence.
SELECT 'signer_credentials_review' AS finding, "TenantId", "Id",
    "SignedByDoctorId" IS NULL OR "SignedByDoctorId" = '00000000-0000-0000-0000-000000000000'::uuid AS missing_signer,
    NULLIF(btrim("DoctorRegistrationNumber"), '') IS NULL AS missing_registration,
    NULLIF(btrim("DoctorQualifications"), '') IS NULL AS missing_qualifications,
    upper(btrim("DoctorRegistrationNumber")) = 'NMC-VERIFIED-PRACTITIONER' AS known_placeholder
FROM "Consultations" WHERE "Status" IN (1, 2) AND
    ("SignedByDoctorId" IS NULL OR "SignedByDoctorId" = '00000000-0000-0000-0000-000000000000'::uuid
     OR NULLIF(btrim("DoctorRegistrationNumber"), '') IS NULL
     OR NULLIF(btrim("DoctorQualifications"), '') IS NULL
     OR upper(btrim("DoctorRegistrationNumber")) = 'NMC-VERIFIED-PRACTITIONER');

-- Same-second supplies and absent amendment timestamps need chronology adjudication.
SELECT 'superseded_prescription_supply_review' AS finding, d."TenantId", d."Id",
    c."Id" AS consultation_id, d."DispensedAt", successor.amended_at
FROM "DispenseRecords" d JOIN "Prescriptions" p
    ON (p."TenantId", p."Id") = (d."TenantId", d."PrescriptionId")
JOIN "Consultations" c ON (c."TenantId", c."Id") = (p."TenantId", p."ConsultationId")
LEFT JOIN LATERAL (
    SELECT min(n."SignedAt") AS amended_at, count(*) AS versions
    FROM "Consultations" n WHERE n."TenantId" = c."TenantId"
        AND n."PreviousVersionId" = c."Id" AND n."Status" IN (1, 2)
) successor ON true
WHERE (c."Status" = 2 OR successor.versions > 0)
    AND (successor.amended_at IS NULL OR d."DispensedAt" >= successor.amended_at);

-- There is no predecessor-item ID. Match the persisted SameMedicine fields, never just
-- drug/name. SQL lower/btrim is a review approximation to .NET ordinal-ignore-case/Trim.
-- Compare direct supply + ONE predecessor's carried count (not every version's totals).
-- Changed/removed/duplicate supplied identities are explicitly ambiguous, not auto-mapped.
WITH items AS (
    SELECT i.*, p."TenantId", c."Id" AS consultation_id, c."PreviousVersionId",
        jsonb_build_array(i."DrugId", lower(btrim(i."GenericName")), lower(btrim(i."BrandName")),
            lower(btrim(i."Strength")), lower(btrim(i."DosageForm")), lower(btrim(i."Route")),
            lower(btrim(i."Frequency")), i."Duration", lower(btrim(i."DurationUnit")),
            i."TotalQuantity", lower(btrim(i."Timing")), lower(btrim(i."SpecialInstructions"))) AS medicine
    FROM "PrescriptionItems" i JOIN "Prescriptions" p ON p."Id" = i."PrescriptionId"
    JOIN "Consultations" c ON (c."TenantId", c."Id") = (p."TenantId", p."ConsultationId")
), history AS (
    SELECT i."Id", COALESCE(sum(s."Quantity"), 0) AS supplied,
        bool_or(s."Id" IS NOT NULL AND (s."Quantity" <= 0 OR d."TenantId" <> i."TenantId"
            OR d."PrescriptionId" IS DISTINCT FROM i."PrescriptionId")) AS invalid_link
    FROM items i LEFT JOIN "DispenseItems" s ON s."PrescriptionItemId" = i."Id"
    LEFT JOIN "DispenseRecords" d ON d."Id" = s."DispenseRecordId" GROUP BY i."Id"
), candidates AS (
    SELECT i."TenantId", i."Id" AS item_id, i.consultation_id,
        CASE WHEN h.invalid_link THEN 'invalid_direct_supply_link'
             WHEN old.matches > 1 THEN 'ambiguous_predecessor_items'
             ELSE 'stored_quantity_disagrees_with_direct_plus_carried' END AS reason
    FROM items i JOIN history h ON h."Id" = i."Id"
    LEFT JOIN LATERAL (
        SELECT count(*) AS matches, COALESCE(sum(o."DispensedQuantity"), 0) AS carried
        FROM items o WHERE (o."TenantId", o.consultation_id) = (i."TenantId", i."PreviousVersionId")
            AND o.medicine = i.medicine
    ) old ON true
    WHERE h.invalid_link OR old.matches > 1
        OR i."DispensedQuantity" <> h.supplied + old.carried
    UNION ALL
    SELECT o."TenantId", o."Id", n."Id", 'supplied_item_removed_changed_or_ambiguous'
    FROM items o JOIN "Consultations" n
        ON (n."TenantId", n."PreviousVersionId") = (o."TenantId", o.consultation_id)
    WHERE o."DispensedQuantity" > 0 AND n."Status" IN (1, 2)
        AND (SELECT count(*) FROM items i WHERE (i."TenantId", i.consultation_id) = (n."TenantId", n."Id")
            AND i.medicine = o.medicine) <> 1
)
SELECT 'fulfillment_history_review' AS finding, * FROM candidates;

-- Stored line tax rate/category only: no inference from today's tenant/drug tax profile.
-- Pharmacy uses reverse GST rounded per line, and its current writers have no line discount.
-- Legacy discounted/other pricing formats are review candidates, not proven corruption.
SELECT 'invoice_discount_tax_basis_review' AS finding, i."TenantId", i."Id"
FROM "Invoices" i JOIN "InvoiceLineItems" l ON l."InvoiceId" = i."Id"
CROSS JOIN LATERAL (VALUES (CASE WHEN i."InvoiceType" = 1
    THEN round(l."Quantity" * l."UnitPrice" / NULLIF(1 + l."GstRate" / 100, 0), 2)
    ELSE l."Quantity" * l."UnitPrice" - l."DiscountAmount" END)) basis(taxable)
GROUP BY i."Id" HAVING i."Subtotal" <> sum(basis.taxable)
    OR i."DiscountAmount" < 0 OR i."DiscountAmount" > i."Subtotal" + i."GstAmount"
    OR bool_or(l."Quantity" <= 0 OR l."UnitPrice" < 0 OR l."DiscountAmount" < 0
        OR (i."InvoiceType" = 1 AND l."DiscountAmount" <> 0)
        OR l."DiscountAmount" > l."Quantity" * l."UnitPrice" OR l."GstRate" < 0 OR l."GstRate" > 100
        OR l."TaxCategory" NOT IN (0, 1, 2)
        OR (l."TaxCategory" IN (0, 1) AND (l."GstRate" <> 0 OR l."TaxAmount" <> 0))
        OR (l."TaxCategory" = 2 AND l."GstRate" = 0)
        OR l."TaxAmount" IS DISTINCT FROM CASE WHEN i."InvoiceType" = 1
            THEN l."Quantity" * l."UnitPrice" - basis.taxable
            ELSE round(basis.taxable * l."GstRate" / 100, 2) END);

-- Expected shape from InvoiceAccounting.PostFinalization. Status/FinalizedAt are leads:
-- legacy Issued/Paid rows can lack a timestamp. Cancelled rows with a timestamp still count.
WITH expected AS (
    SELECT i."TenantId", i."Id", a.account, a.debit, a.credit
    FROM "Invoices" i CROSS JOIN LATERAL (VALUES
        ('AccountsReceivable', i."Total", 0::numeric, true),
        (CASE WHEN i."InvoiceType" = 1 THEN 'PharmacySales' ELSE 'ConsultationRevenue' END, 0, i."Subtotal", true),
        ('GstPayable', 0, i."GstAmount", i."GstAmount" > 0),
        ('DiscountAllowed', i."DiscountAmount", 0, i."DiscountAmount" > 0)
    ) a(account, debit, credit, needed)
    WHERE (i."FinalizedAt" IS NOT NULL OR i."Status" IN (1, 2, 3, 5, 6)) AND a.needed
), actual AS (
    SELECT "TenantId", "ReferenceId" AS "Id", "AccountCategory" AS account,
        sum("DebitAmount") AS debit, sum("CreditAmount") AS credit
    FROM "FinanceLedgerEntries" WHERE "EntryType" = 0 AND "ReferenceType" = 'Invoice'
    GROUP BY "TenantId", "ReferenceId", "AccountCategory"
)
SELECT 'invoice_finalization_posting_review' AS finding,
    COALESCE(e."TenantId", a."TenantId") AS tenant_id, COALESCE(e."Id", a."Id") AS invoice_id,
    array_agg(COALESCE(e.account, a.account) ORDER BY COALESCE(e.account, a.account)) AS accounts
FROM expected e FULL JOIN actual a USING ("TenantId", "Id", account)
WHERE e.debit IS DISTINCT FROM a.debit OR e.credit IS DISTINCT FROM a.credit
GROUP BY COALESCE(e."TenantId", a."TenantId"), COALESCE(e."Id", a."Id");

-- Payment/refund cash-bank legs reference the payment; AR legs reference the invoice.
-- AdvanceDeposit uses DepositApplied(7)/PatientDepositAllocation/PatientDeposits, not Bank.
-- Invoice-side sums cannot establish individual pair linkage, timing or provider settlement.
-- Unexpected/orphan postings of these types also surface, including failed/pending payouts.
WITH receipts AS (
    SELECT *, CASE WHEN "Amount" < 0 THEN 2 WHEN "Method" = 6 THEN 7 ELSE 1 END AS entry_type
    FROM "Payments" WHERE "VerificationStatus" = 2
        AND (("Amount" > 0 AND "Status" = 1) OR ("Amount" < 0 AND "Status" = 3))
), legs AS (
    SELECT r."TenantId", r.entry_type, v.reference_type, v.reference_id, v.account, v.debit, v.credit
    FROM receipts r CROSS JOIN LATERAL (VALUES
        (CASE WHEN r."Amount" < 0 THEN 'Refund' WHEN r."Method" = 6 THEN 'PatientDepositAllocation' ELSE 'Payment' END,
         r."Id", CASE WHEN r."Amount" > 0 AND r."Method" = 6 THEN 'PatientDeposits'
                     WHEN r."Method" = 0 THEN 'Cash' ELSE 'Bank' END,
         greatest(r."Amount", 0), greatest(-r."Amount", 0)),
        ('Invoice', r."InvoiceId", 'AccountsReceivable', greatest(-r."Amount", 0), greatest(r."Amount", 0))
    ) v(reference_type, reference_id, account, debit, credit)
), expected AS (
    SELECT "TenantId", entry_type, reference_type, reference_id, account, sum(debit) AS debit, sum(credit) AS credit
    FROM legs GROUP BY "TenantId", entry_type, reference_type, reference_id, account
), actual AS (
    SELECT "TenantId", "EntryType" AS entry_type, "ReferenceType" AS reference_type,
        "ReferenceId" AS reference_id, "AccountCategory" AS account,
        sum("DebitAmount") AS debit, sum("CreditAmount") AS credit
    FROM "FinanceLedgerEntries" WHERE "EntryType" IN (1, 2, 7)
    GROUP BY "TenantId", "EntryType", "ReferenceType", "ReferenceId", "AccountCategory"
)
SELECT 'payment_refund_journal_review' AS finding, COALESCE(e."TenantId", a."TenantId") AS tenant_id,
    COALESCE(e.entry_type, a.entry_type) AS entry_type, COALESCE(e.reference_type, a.reference_type) AS reference_type,
    COALESCE(e.reference_id, a.reference_id) AS reference_id,
    array_agg(COALESCE(e.account, a.account) ORDER BY COALESCE(e.account, a.account)) AS accounts
FROM expected e FULL JOIN actual a USING ("TenantId", entry_type, reference_type, reference_id, account)
WHERE e.debit IS DISTINCT FROM a.debit OR e.credit IS DISTINCT FROM a.credit
GROUP BY COALESCE(e."TenantId", a."TenantId"), COALESCE(e.entry_type, a.entry_type),
    COALESCE(e.reference_type, a.reference_type), COALESCE(e.reference_id, a.reference_id);

-- A stale Paid status can disagree even when the stored aggregates agree with each other.
SELECT 'paid_invoice_status_review' AS finding, "TenantId", "Id"
FROM "Invoices" WHERE "Status" = 2 AND ("BalanceDue" <> 0 OR "PaidAmount" + "CreditedAmount" < "Total");

-- No persisted offline-origin flag exists; retry keys are shared by online registration.
-- Review ALL patients, then adjudicate origin from authorized operational evidence.
-- Current demographics are compared at registration date; later edits require audit review.
-- A later withdrawal does not erase captured care consent. Later-only consent is a lead.
-- UHID checks are presence/tenant uniqueness, not a guessed legacy-format validator.
WITH reviewed AS (
    SELECT p."TenantId", p."Id", array_remove(ARRAY[
        CASE WHEN length(btrim(p."Name")) NOT BETWEEN 2 AND 100 OR btrim(p."Phone") !~ '^[6-9][0-9]{9}$'
            OR p."Gender" NOT IN (0, 1, 2, 3) OR length(p."Address") > 500
            OR (p."Dob" IS NULL AND p."ApproxAge" IS NULL) OR p."ApproxAge" NOT BETWEEN 0 AND 130
            OR p."DobHasValue" <> (p."Dob" IS NOT NULL)
            OR p."Dob" > (p."CreatedAt" AT TIME ZONE 'Asia/Kolkata')::date
            OR p."Dob" < (p."CreatedAt" AT TIME ZONE 'Asia/Kolkata')::date - INTERVAL '130 years'
            THEN 'demographics' END,
        CASE WHEN NULLIF(btrim(p."Uhid"), '') IS NULL THEN 'missing_uhid'
             WHEN EXISTS (SELECT 1 FROM "Patients" other WHERE other."TenantId" = p."TenantId"
                 AND other."Id" <> p."Id" AND btrim(other."Uhid") = btrim(p."Uhid")) THEN 'duplicate_uhid' END,
        CASE WHEN NOT EXISTS (SELECT 1 FROM "PatientConsents" c
            WHERE (c."TenantId", c."PatientId") = (p."TenantId", p."Id")
                AND btrim(c."Purpose") IN ('care_delivery', 'direct_care')
                AND NULLIF(btrim(c."NoticeVersion"), '') IS NOT NULL AND NULLIF(btrim(c."Language"), '') IS NOT NULL
                AND NULLIF(btrim(c."CaptureMethod"), '') IS NOT NULL
                AND c."CapturedBy" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND (c."CapturedAt" AT TIME ZONE 'Asia/Kolkata')::date <= (p."CreatedAt" AT TIME ZONE 'Asia/Kolkata')::date
                AND (c."WithdrawnAt" IS NULL OR c."WithdrawnAt" >= c."CapturedAt")
                AND (CASE WHEN p."Dob" IS NOT NULL THEN
                        p."Dob" <= (p."CreatedAt" AT TIME ZONE 'Asia/Kolkata')::date - INTERVAL '18 years'
                     ELSE p."ApproxAge" >= 18 END
                     OR (NULLIF(btrim(c."GuardianName"), '') IS NOT NULL
                         AND NULLIF(btrim(c."GuardianRelationship"), '') IS NOT NULL)))
            THEN 'care_consent_or_guardian_evidence' END
    ], NULL) AS reasons FROM "Patients" p
)
SELECT 'patient_registration_review' AS finding, * FROM reviewed WHERE cardinality(reasons) > 0;

-- Include reserved pending refunds in capacity. A missing manual source can be legitimate;
-- a supplied source must be a confirmed collection on the SAME tenant and invoice.
-- Current state/sums cannot reconstruct every past approval, payment transition or balance.
WITH capacity AS (
    SELECT "TenantId", "InvoiceId",
        COALESCE(sum("Amount") FILTER (WHERE "Amount" > 0 AND "Status" = 1 AND "VerificationStatus" = 2), 0) AS collected,
        COALESCE(sum(-"Amount") FILTER (WHERE "Amount" < 0 AND "Status" IN (0, 3)), 0) AS reserved
    FROM "Payments" GROUP BY "TenantId", "InvoiceId"
)
SELECT 'refund_origin_capacity_review' AS finding, r."TenantId", r."Id", r."InvoiceId",
    c.collected, c.reserved, r."RefundSourcePaymentId"
FROM "Payments" r JOIN capacity c USING ("TenantId", "InvoiceId")
LEFT JOIN "Payments" source ON (source."TenantId", source."InvoiceId", source."Id") =
    (r."TenantId", r."InvoiceId", r."RefundSourcePaymentId")
WHERE r."Amount" < 0 AND r."Status" IN (0, 3) AND (c.reserved > c.collected
    OR (r."Method" = 5 AND r."RefundSourcePaymentId" IS NULL)
    OR (r."RefundSourcePaymentId" IS NOT NULL AND (source."Id" IS NULL OR source."Amount" <= 0
        OR source."Status" <> 1 OR source."VerificationStatus" <> 2
        OR (r."Method" = 5 AND (source."Method" <> 5 OR NULLIF(btrim(source."RazorpayPaymentId"), '') IS NULL
            OR r."RazorpayPaymentId" IS DISTINCT FROM source."RazorpayPaymentId"))
        OR (r."Status" = 3 AND source."PaidAt" > r."PaidAt")
        OR source."Amount" < (SELECT sum(-other."Amount") FROM "Payments" other
            WHERE other."TenantId" = r."TenantId" AND other."RefundSourcePaymentId" = source."Id"
                AND other."Amount" < 0 AND other."Status" IN (0, 3))))
    OR (r."Status" = 3 AND r."PaidAt" IS NOT NULL AND -r."Amount" >
        COALESCE((SELECT sum(p."Amount") FROM "Payments" p WHERE (p."TenantId", p."InvoiceId") = (r."TenantId", r."InvoiceId")
            AND p."Amount" > 0 AND p."Status" = 1 AND p."VerificationStatus" = 2 AND p."PaidAt" <= r."PaidAt"), 0)));

-- Exact known test-response IDs only (Part3FinancialPostgresTests, Part1RefundSafetyTests,
-- frontend/e2e/part1-billing-contracts.spec.ts). Candidates for provider reconciliation,
-- NOT proof of fabrication. No retained legacy generator pattern is asserted here.
-- All other provider IDs still require provider-side evidence; shape/prefix proves nothing.
SELECT 'known_refund_provider_placeholder_review' AS finding, "TenantId", "Id", "RefundReference"
FROM "Payments" WHERE "Amount" < 0 AND "Method" = 5 AND "Status" IN (0, 3)
    AND btrim("RefundReference") IN ('rfnd_synthetic', 'rfnd_real123', 'rfnd_provider123');

-- Current full-return contract: one inward Return movement per invoice batch, linked to
-- refund.Id. Compare batch quantities and drug identity; inventory balances alone prove nothing.
-- Legacy invoice-linked/unknown Return references surface separately for pharmacist review.
-- This does not reconstruct missing historical outgoing sales or controlled-drug registers.
SELECT 'refund_stock_evidence_review' AS finding, r."TenantId", r."Id" AS reference_id,
    'refund_return_contract' AS reason
FROM "Payments" r JOIN "Invoices" i ON (i."TenantId", i."Id") = (r."TenantId", r."InvoiceId")
WHERE r."Amount" < 0 AND (
    (r."IsPharmacyReturn" AND r."Status" = 3 AND r."VerificationStatus" = 2 AND
        (i."InvoiceType" <> 1 OR -r."Amount" <> i."Total"
         OR NOT EXISTS (SELECT 1 FROM "InvoiceLineItems" l WHERE l."InvoiceId" = i."Id")
         OR EXISTS (SELECT 1 FROM "InvoiceLineItems" l LEFT JOIN "DrugBatches" b
                ON b."Id" = l."DrugBatchId" AND b."TenantId" = r."TenantId"
             WHERE l."InvoiceId" = i."Id" AND (l."Quantity" <= 0 OR b."Id" IS NULL))
         OR EXISTS (SELECT 1 FROM "Payments" other WHERE (other."TenantId", other."InvoiceId") = (r."TenantId", r."InvoiceId")
             AND other."Id" <> r."Id" AND other."Amount" < 0 AND other."IsPharmacyReturn"
             AND other."Status" = 3 AND other."VerificationStatus" = 2)
         OR EXISTS (
             SELECT 1 FROM (SELECT "DrugBatchId", sum("Quantity") AS quantity FROM "InvoiceLineItems"
                 WHERE "InvoiceId" = i."Id" GROUP BY "DrugBatchId") expected
             FULL JOIN (SELECT m."DrugBatchId", sum(m."Quantity") AS quantity,
                     bool_or(m."Direction" <> 0 OR m."Quantity" <= 0 OR m."DrugId" IS DISTINCT FROM b."DrugId") AS invalid,
                     count(*) AS movements
                 FROM "DrugStockMovements" m LEFT JOIN "DrugBatches" b ON (b."TenantId", b."Id") = (m."TenantId", m."DrugBatchId")
                 WHERE m."TenantId" = r."TenantId" AND m."ReferenceType" = 'Return' AND m."ReferenceId" = r."Id"
                 GROUP BY m."DrugBatchId") actual USING ("DrugBatchId")
             WHERE expected.quantity IS DISTINCT FROM actual.quantity OR actual.invalid OR actual.movements <> 1)))
    OR ((NOT r."IsPharmacyReturn" OR r."Status" <> 3 OR r."VerificationStatus" <> 2)
        AND EXISTS (SELECT 1 FROM "DrugStockMovements" m WHERE m."TenantId" = r."TenantId"
            AND m."ReferenceType" = 'Return' AND m."ReferenceId" = r."Id")))
UNION ALL
SELECT 'refund_stock_evidence_review', m."TenantId", m."ReferenceId", 'unresolved_return_reference'
FROM "DrugStockMovements" m WHERE m."ReferenceType" = 'Return' AND NOT EXISTS
    (SELECT 1 FROM "Payments" r WHERE (r."TenantId", r."Id") = (m."TenantId", m."ReferenceId") AND r."Amount" < 0)
GROUP BY m."TenantId", m."ReferenceId";
ROLLBACK;
