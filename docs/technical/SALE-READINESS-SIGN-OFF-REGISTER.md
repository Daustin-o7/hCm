# Hospital CRM: Master First-Sale Readiness Sign-Off Register

**Milestone:** Production Go-Live & First Paid Clinic Activation  
**Release Target:** Hospital_CRM v1.0.0-rc  
**Reference Specification:** [`master-readiness-plan.md`](file:///C:/Users/derek/.gemini/antigravity/brain/1af3db7f-9709-458c-857b-145476bc31bb/master-readiness-plan.md)  
**Date Generated:** 2026-11-09  
**Paid Activation Status:** **STRICTLY NO-GO (PENDING EXTERNAL DEPLOYMENT & NAMED SIGN-OFFS)**  

---

## 1. 18-Gate Verification Matrix (G01–G18)

| Gate | Title | Stream | Verification Status | Code & Artifact Evidence |
|---|---|---|---|---|
| **G01** | Release Verification | Deployment | **INTEGRATED LOCALLY (SNAPSHOT FREEZE & LINUX CI PENDING)** | Full test suite passing locally (1,261 passed / 0 failed / 70 skipped). Release commit snapshot freeze, Linux/container build, and portable evidence retention pending. |
| **G02** | Tenant Isolation & DB Outage | Engineering | **HANDLER IMPLEMENTED (ISOLATION MATRIX & TARGET DRILL PENDING)** | [`DECISION-DATABASE-OUTAGE-500-VS-503.md`](file:///e:/Company/Hospital%20Management/Hospital_CRM/docs/technical/DECISION-DATABASE-OUTAGE-500-VS-503.md), [`DatabaseOutageExceptionHandler.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Middlewares/DatabaseOutageExceptionHandler.cs) (requires Npgsql provenance, returns RFC 7807 503 `database_unavailable`). 24-row isolation matrix against non-owner PostgreSQL runtime and target container outage drill pending. |
| **G03** | Offline Privacy & MDM | Governance | **AWAITING SIGN-OFF** | IndexedDB AES-GCM encryption verified in browser tests. Awaiting clinic MDM policy execution and hardware ownership verification. |
| **G04** | Live Workflows & PWA | Target Rehearsal | **READY FOR STAGING** | PWA precache verified (46 assets). Offline outbox replay ready for physical device verification and journal reconciliation. |
| **G05** | Printing & Linux Fonts | Deployment | **VERIFIED IN CODE** | `libfontconfig1` in Dockerfile. Embedded Noto Sans / Devanagari TTF resources in [`PrescriptionPdfCanvas.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Services/PrescriptionPdfCanvas.cs). Physical sample print pending. |
| **G06** | Accounting & Tax Basis | Engineering | **ENGINEERING IMPLEMENTED (CA SIGN-OFF PENDING)** | GST Rule C in [`InvoiceGstRecognition.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Services/InvoiceGstRecognition.cs) (cumulative collection basis for management reporting; approved CN adjusts in issue period; refund alone does not reverse GST); Fix 2 in [`InvoicesController.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Controllers/InvoicesController.cs) & migration `20261109084838_RefundCreditNoteResolution`; Fix 3 daily shift handover in [`Billing.tsx`](file:///e:/Company/Hospital%20Management/Hospital_CRM/frontend/src/pages/Billing.tsx) & [`LedgerController.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Controllers/LedgerController.cs); [`STATUTORY-GSTR1-DISCOUNT-ALLOCATION.md`](file:///e:/Company/Hospital%20Management/Hospital_CRM/docs/technical/STATUTORY-GSTR1-DISCOUNT-ALLOCATION.md) (statutory invoice-date basis). Formal CA signature pending. |
| **G07** | No-Visit Policy Engine | Engineering | **ENGINEERING IMPLEMENTED (CLINICAL SIGN-OFF PENDING)** | Production [`NoVisitPolicyEngine.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Services/NoVisitPolicyEngine.cs) extracted and validated against 9 golden fixture examples in [`NoVisitPolicyValidationTests.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/tests/Hospital_CRM.Tests/NoVisitPolicyValidationTests.cs). Dispatch worker remains disabled (`no_visit_policy_pending`) pending Clinical Director approval. |
| **G08** | Identity Recovery | Security | **READY FOR TARGET** | Data Protection key ring continuity, 30-min password reset expiry, instant old-link invalidation verified. |
| **G09** | Real Email Onboarding | Deployment | **READY FOR SMTP** | [`EmailSender.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Services/EmailSender.cs) retry/backoff implemented. Awaiting production SMTP credentials and external inbox confirmation. |
| **G10** | Jobs & Notification Recovery | Target Rehearsal | **VERIFIED IN CODE** | Append-only `NotificationIntents` table, 24h deduplication, and crash replay verified. WhatsApp sandbox webhook pending. |
| **G11** | Private Storage & Downloads | Security | **VERIFIED IN CODE** | `LabFileStorage` AES-GCM envelope encryption, short-lived signed download tokens. Awaiting Azure Blob private endpoint wiring. |
| **G12** | Production Identity & MFA | Security | **VERIFIED IN CODE** | OpenIddict RS256 token issuer, PKCE, production mandatory MFA (`Mfa:RequireEnrolment = true`). |
| **G13** | Disaster Recovery Drill | Operations | **SCRIPTS CHECKED IN** | [`scripts/backup-db.ps1|sh`](file:///e:/Company/Hospital%20Management/Hospital_CRM/scripts/backup-db.ps1), [`scripts/restore-db.ps1|sh`](file:///e:/Company/Hospital%20Management/Hospital_CRM/scripts/restore-db.ps1), [`scripts/verify-runtime-role.sql`](file:///e:/Company/Hospital%20Management/Hospital_CRM/scripts/verify-runtime-role.sql). Target rehearsal pending. |
| **G14** | Payments & Reconciliation | Target Rehearsal | **VERIFIED IN CODE** | Razorpay constant-time HMAC signature verification, idempotent capture and refund handling. Sandbox replay pending. |
| **G15** | Historical Records Adjudication | Governance | **QUERIES READY** | 26 diagnostic queries in [`scripts/sale-readiness-preflight.sql`](file:///e:/Company/Hospital%20Management/Hospital_CRM/scripts/sale-readiness-preflight.sql). Review panel disposition pending. |
| **G16** | Dependency Security | Security | **FORMAL ANALYSIS DOCUMENTED (NAMED SIGN-OFF PENDING)** | [`FRONTEND-DEPENDENCY-DISPOSITION-2026-11-09.md`](file:///e:/Company/Hospital%20Management/Hospital_CRM/docs/technical/FRONTEND-DEPENDENCY-DISPOSITION-2026-11-09.md) formally analyzed 4 moderate / 2 low build-time packages with 0 runtime exposure. Named security sign-off pending. |
| **G17** | Pharmacy & Mobile Scope | Engineering | **VERIFIED IN CODE** | Form 20/21 license fail-closed gate in `PharmacyPosController.cs`. Server-side module gate in [`ModuleGateMiddleware.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Middlewares/ModuleGateMiddleware.cs) returning 403 `module_disabled` on unlicensed tenants. |
| **G18** | Business & Legal Approval | Governance | **AWAITING SIGN-OFF** | Pricing tier matrix, contract templates (MSA/DPA/SLA), and clinic dry runs pending executive execution. |

---

## 2. Sign-Off & Acceptance Register

### A. Clinical Governance Sign-Off (CMO / Lead Clinician)
*I confirm that I have reviewed the clinical consultation workflow, digital prescription generation, English/Hindi HarfBuzz printing, and clinical amendment audit trails. No patient clinical data can be silently overwritten.*

- **Name:** __________________________________
- **Designation / Medical Council Reg No:** __________________________________
- **Decision:** [  ] APPROVED   [  ] REJECTED   [  ] CONDITIONAL
- **Signature:** __________________________________   **Date:** _______________

---

### B. Financial & Statutory Sign-Off (CFO / Senior Chartered Accountant)
*I confirm that I have reviewed the accounting basis: management accounting uses cumulative collection-based recognition (Rule C: GST recognized on collections; refunds alone do not reverse GST; approved Section 34 Credit Notes adjust cumulative recognition in the Credit Note issue period); statutory GSTR-1 export operates on its distinct invoice-date basis with the caveat regarding invoice-level discount allocation (`STATUTORY-GSTR1-DISCOUNT-ALLOCATION.md`). The ledger daily collection tender deductions (Gross, Refunds, Net) reconcile with physical cash and bank settlements.*

- **Name:** __________________________________
- **ICAI Membership No / Designation:** __________________________________
- **Decision:** [  ] APPROVED   [  ] REJECTED   [  ] CONDITIONAL
- **Signature:** __________________________________   **Date:** _______________

---

### C. Security, Privacy & Compliance Sign-Off (DPO / CISO)
*I confirm that database least-privilege roles (`app_user`), append-only audit enforcement, OpenIddict JWT RS256 token issuance, mandatory MFA enrolment, RFC 7807 503 outage contracts, AES-GCM private storage, and the build-time dependency disposition (`FRONTEND-DEPENDENCY-DISPOSITION-2026-11-09.md`) meet production standards.*

- **Name:** __________________________________
- **Designation:** __________________________________
- **Decision:** [  ] APPROVED   [  ] REJECTED   [  ] CONDITIONAL
- **Signature:** __________________________________   **Date:** _______________

---

### D. Operations & SRE Sign-Off (Release Engineer / SRE Lead)
*I confirm that the Linux container deployment cold starts cleanly, all healthchecks pass, Hangfire crash recovery and notification intent deduplication are verified, and backup/restore scripts (`backup-db.sh`, `restore-db.sh`) maintain runtime role grants and data integrity with acceptable RPO (< 15m) and RTO (< 60m).*

- **Name:** __________________________________
- **Designation:** __________________________________
- **Decision:** [  ] APPROVED   [  ] REJECTED   [  ] CONDITIONAL
- **Signature:** __________________________________   **Date:** _______________

---

### E. Executive Commercial Sign-Off (Managing Director / Clinic Owner)
*Having verified that Engineering, Clinical, Financial, Security, and Operational gates are satisfied, I authorize the First-Paid-Activation and Production Go-Live of Hospital CRM for the designated pilot clinic.*

- **Authorized Clinic Name:** __________________________________
- **Subscribed Tier & Modules:** __________________________________
- **Name:** __________________________________
- **Title:** __________________________________
- **Formal Decision:** [  ] **GO FOR PRODUCTION**   [  ] **HOLD / NO-GO**
- **Signature:** __________________________________   **Date:** _______________
