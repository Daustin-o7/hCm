# PaidAmount repair — preflight and backup

Migration `20261110170832_RepairLegacyPaidAmounts` backfills `Invoices.PaidAmount` on legacy rows
that were marked `Paid` with `BalanceDue = 0` but never had the collected amount written. Without it,
those invoices report ₹0 collected in collection reports and the daily ledger.

It is guarded: it only touches rows where a completed payment ledger fully explains the total.
It has no rollback, because the pre-repair state is the defect.

**Run this before `dotnet ef database update` on any database you did not create yourself.**

## 1. Count what will change

```sql
-- Status 2 = Paid. Mirrors the migration's WHERE clause exactly.
SELECT COUNT(*) AS rows_to_repair,
       COALESCE(SUM(i."Total"), 0) AS total_value
FROM "Invoices" i
WHERE i."Status" = 2
  AND i."PaidAmount" = 0
  AND i."Total" > 0
  AND i."BalanceDue" = 0
  AND EXISTS (SELECT 1 FROM "Payments" p WHERE p."InvoiceId" = i."Id" AND p."Status" = 1)
  AND (SELECT COALESCE(SUM(p."Amount"), 0) FROM "Payments" p
       WHERE p."InvoiceId" = i."Id" AND p."Status" = 1) = i."Total";
```

`rows_to_repair = 0` → the migration is a no-op on this database. Continue normally.

## 2. Back up the affected rows (if the count is not zero)

Take a full backup first:

```bash
pg_dump --format=custom --file=hospital_crm_pre_paidamount.dump \
  --host=<host> --port=5432 --username=<owner> --dbname=hospital_crm
```

Use the password prompt or an approved `.pgpass` file; do not put credentials on the command line.

If a full dump is not possible in the window, at minimum export the affected rows:

```sql
\copy (SELECT "Id", "Status", "Total", "PaidAmount", "BalanceDue", "UpdatedAt" FROM "Invoices" i WHERE i."Status" = 2 AND i."PaidAmount" = 0 AND i."Total" > 0 AND i."BalanceDue" = 0 AND EXISTS (SELECT 1 FROM "Payments" p WHERE p."InvoiceId" = i."Id" AND p."Status" = 1)) TO 'paid_amount_before.csv' CSV HEADER
```

## 3. Apply and verify

```bash
dotnet ef database update --project backend/Hospital_CRM.Infrastructure \
  --startup-project backend/Hospital_CRM.Api \
  --connection "<owner connection string>"
```

```sql
-- Investigate any remaining rows: invoices without a matching completed payment ledger
-- are deliberately not repaired by this migration. Do not invent a payment to clear them.
SELECT COUNT(*) FROM "Invoices" WHERE "Status" = 2 AND "PaidAmount" = 0 AND "Total" > 0;

-- Must return 0: no Paid invoice claims more collected than it billed.
SELECT COUNT(*) FROM "Invoices" WHERE "Status" = 2 AND "PaidAmount" > "Total";
```

`ConfirmPayment` now rejects internally inconsistent invoices, so a row that fails either check is
a data defect to investigate, not something to patch by hand.
