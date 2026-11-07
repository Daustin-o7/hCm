# Go-Live Checklist

Provisional switches and database configurations that must be settled **before production traffic**. Add an item here every time code is relaxed for local development or when security policies are enforced.

---

## 1. Security & Authentication

- [ ] **Mandatory MFA enrolment for privileged roles is switched off in Development.**
  - **What is off:** `Mfa:RequireEnrolment` is `false` in `backend/Hospital_CRM.Api/appsettings.Development.json`.
    While it is false, `ClinicAdmin` / `Owner` / `Doctor` / `Receptionist` / `PlatformAdmin` sessions are issued
    without the `mfa_enrolment_required` claim, so the console does not force `/setup-mfa`, and such a user may
    also switch the second factor off themselves (`POST /auth/mfa/disable`).
  - **Production behaviour:** the key is absent from `appsettings.json`, so `MfaGateOn` defaults to `true`
    (`AuthController`) and `MfaPolicy.RequiresEnrolment` is enforced exactly as specified in Section 0.
  - **Verify before launch:**
    ```bash
    # must print nothing (key only exists in the Development file)
    Select-String -Path backend/Hospital_CRM.Api/appsettings*.json -Pattern "RequireEnrolment"
    # must answer "true" on a production-like host
    curl -s https://<api>/api/v1/auth/login -H "Content-Type: application/json" \
      -d '{"email":"<doctor>","password":"<pw>"}' | grep -o '"mfaEnrolmentRequired":[a-z]*'
    ```
    Also confirm the environment variables `Mfa__RequireEnrolment` / `ASPNETCORE_ENVIRONMENT` are not set to
    `false` / `Development` in the production deployment (docker compose, App Service, etc.).
  - **Risk if missed:** privileged accounts can operate without a second factor — a stolen password is enough
    to move money, write clinical notes, or administer the platform.

- [ ] **Deploy the authorized queue-link expiry policy (completion D1, 2026-11-07).**
  - **Policy:** opaque `qtrk_` links expire **24 hours after actual check-in**, shortened to **two hours after
    Completed, Cancelled or NoShow**, whichever expires first. Midnight, appointment date, client timezone,
    routine updates and repeat check-in do not extend the persisted deadline.
  - **Rollout:** apply additive migration `20261107102726_QueueTrackingExpiry` and deploy the API and queue
    page together. Nullable `Appointment.QueueTrackingExpiresAt` has **no historical backfill**. Existing
    missing-expiry links fail closed; reception must use the legitimate reschedule/check-in workflow when
    appropriate. Reschedule clears both tokens and the deadline; a subsequent check-in issues a new token.
  - **Response contract:** invalid, unknown, missing-expiry and expired links return generic
    `404 { error: "token_not_found" }`, before disclosing owner subscription state. A valid link uses its
    owner's tenant independently of ambient identity. Responses are `no-store`; terminal responses contain
    only `status` and `expiresInSeconds`. The page stops terminal polling, clears data at expiry using server
    time remaining, and rejects late responses.
  - **Local proof:** `M12_Queue_ExactTokenScope_BoundedExpiryAndTerminalPrivacy`, the HTTP lifecycle regression
    and public-token scope pass in the **18/18** migrated PG16.15 run; **8/8 intercepted Chromium** contracts
    pass against a production preview. See [Part 3 current-source verification](SALE-READINESS-PART-3-COMPLETION-2026-11-07.md#current-source-verification--queue-link-d1).
  - **Verify before launch:** apply the same release/migrations/grants and check expiry, owner isolation,
    cache headers and phone/PWA behavior on the deployment target. Product approval of this policy does not
    close deployment or Section 8 release sign-offs. It supersedes the earlier permanent-link proposal.

---

## 2. Database Governance & Append-Only Grants (FR-08)

- [ ] **Database Role Permissions & Audit Table Immutability:**
  Run the checked-in `scripts/setup-least-privilege-db.sql` as the database owner after every migration batch.
  It revokes `UPDATE`, `DELETE` and `TRUNCATE` from `app_user` on all 18 protected history tables:
  `PatientAuditLogs`, `NurseAuditLogs`, `NotificationLogs`, `PriorityLogs`, `ImpersonationLogs`,
  `ImpersonationPhiAccessLogs`, `AppointmentHistories`, `ControlledSubstanceRegisters`, `AuthAuditLogs`,
  `DrugStockMovements`, `StockMovements`, `MedicineSearchMissLogs`, `PrescriptionPrintLogs`,
  `FinanceLedgerEntries`, `InvoicePrintLogs`, `CreditNotes`, `NotificationIntents`, `TenantProvisioningReceipts`.
  Only these narrow column updates survive: `ImpersonationLogs.EndedAt`, `CreditNotes.Status` /
  `SecondApprovedByUserId` / `IssuedAt`, `NotificationIntents.HandedOffAt`,
  `TenantProvisioningReceipts.ProtectedResponse`.
  - **Verify before launch:** run `scripts/verify-runtime-role.sql` **as `app_user`** (not the owner) against the
    production database; it must exit 0. Any new audit table must be added to both scripts and to the
    `M24_EveryProtectedHistoryTable…` regression, which fails if the lists drift.

- [ ] **Production Composite Indexes:**
  Confirm composite indexes are applied to PostgreSQL for low-latency queries during peak OPD volume:
  ```sql
  CREATE INDEX IF NOT EXISTS "IX_Patients_TenantId_Phone" ON "Patients" ("TenantId", "Phone");
  CREATE INDEX IF NOT EXISTS "IX_Appointments_TenantId_DoctorId_Date_Status" ON "Appointments" ("TenantId", "DoctorId", "Date", "Status");
  CREATE INDEX IF NOT EXISTS "IX_DrugBatches_DrugId_ExpiryDate_Remaining" ON "DrugBatches" ("DrugId", "ExpiryDate", "QuantityRemaining");
  CREATE INDEX IF NOT EXISTS "IX_Invoices_TenantId_PatientId_Status" ON "Invoices" ("TenantId", "PatientId", "Status");
  CREATE INDEX IF NOT EXISTS "IX_Consultations_TenantId_PatientId_CreatedAt" ON "Consultations" ("TenantId", "PatientId", "CreatedAt");
  ```

---

## 3. Regulatory Gates (CDSCO Drugs & Cosmetics Act)

- [ ] **Retail Drug Licenses (Form 20 & Form 21):**
  Pharmacy POS dispensary requires active Form 20 (General Drugs) and Form 21 (Biological / Schedule C/C1) licenses configured on the clinic:
  - Verify `Clinics.PharmacyLicenceNumber20` and `Clinics.PharmacyLicenceNumber21` are populated.
  - If missing, POS checkout returns `403 pharmacy_licence_missing`.

---

## 4. Tenancy & Activated Modules

- [ ] **Activated Modules on Tenants & Clinics:**
  Ensure the tenant has all licensed modules activated in `Tenants.ActivatedModules`:
  `core,pharmacy,lab,inventory,finance,wishlist,emergency,precheck,notifications,staff_roster`.
