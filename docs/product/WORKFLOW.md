# Hospital CRM — Complete Workflow (Phase 1 + Phase 2)

> Single source of truth for what the system does, end-to-end. Covers every FR from FR-01 through FR-14-XX.

---

## System Overview

```
Hospital CRM (Samstack AI)
├── Phase 1: FR-01 → FR-22 (core CRM + billing) — SHIPPED
├── Phase 2: FR-23 → FR-14 (9 Fast-Follow modules) — SHIPPED 100%
│   ├── MOD-23 Pre-Check Form
│   ├── MOD-24 Emergency Queue
│   ├── MOD-25 Live Ticket Tracking
│   ├── MOD-12 Speciality EMR Templates
│   ├── MOD-13 Notification Rules Engine (absorbs FR-20/21)
│   ├── MOD-09 Inventory
│   ├── MOD-10 Wishlist
│   ├── MOD-08 Lab Records
│   ├── MOD-11 Finance Ledger
│   └── MOD-14 Platform Admin Portal
└── Pharmacy Suite (Track 2 — 4 Dedicated Modular Controllers) — SHIPPED
    ├── PharmacyCatalogController (MOD-15 Drug Catalog & Generic Substitutes)
    ├── PharmacyBatchController (MOD-15 FEFO Batch Tracking & PO Receive)
    ├── PharmacyPosController (MOD-15/16 POS Checkout, Returns & Stats)
    └── PharmacyComplianceController (MOD-16/17 Rx Dispensing, Schedule Registers & Suppliers)
```

**Stack:** .NET 10 (Minimal APIs, EF Core 10) · React 19 PWA · PostgreSQL 16 · OpenIddict (In-House OIDC) · Razorpay · WhatsApp Business API

---

## Section 0 — Identity, Tenancy & Module Gateway

> The foundational security, isolation, and entitlement architecture governing all clinic access.

```
0. IDENTITY, TENANCY & MODULE GATEWAY
 • Login (rate-limited, lockout) ► BCrypt cost 12 verify ► MFA (doctor, receptionist/billing, admin)
 • Global User ► TenantMemberships[] (user, tenant, clinic, role) ► picks clinic ► token scoped to ONE tenant
 • RS256 JWT, 15 min: claims = sub, email, role, tenant_id, clinicId, mfa_enrolment_required; header kid
 • Refresh token: SHA-256 hash in DB, rotated, family-revoked on reuse, httpOnly cookie only
 • Gateway checks, in order, on every request:
     1. Tenant status (trial / active / past_due / suspended)
     2. Module entitlement [RequireModule] (cached 30 s, server-side, + module dependencies)
     3. Feature flag (TenantFeatureFlags rollout / kill switch)
     4. Role ([AuthorizeRoles]) + permission ([RequirePermission]) policies
     5. Clinical-data scope (same tenant; treating relationship where applicable)
 • Isolation: EF Core global tenant filter + Postgres RLS (SET LOCAL app.current_tenant_id)
 • Audit log: auth events (AuthAuditLogs), role changes, break-glass (PatientAuditLogs), support impersonation
```

### 0.1 — Architecture & Request Pipeline

```
[ Incoming Client Request ]
           │
           ▼
 0. Authentication (UseAuthentication — JWT RS256, kid resolved against JWKS)
    └─► Tenant id comes from the signed `tenant_id` claim only. Never a body, header or URL.
           │
           ▼
 1. Tenant Status Gate          (SubscriptionEnforcementMiddleware)
    └─► SubscriptionPolicy.EffectiveAccess(tenant.SubscriptionStatus, tenant.SubscriptionEndsAt)
        ├─ Trial / Active / GracePeriod ──► Continue to Step 2
        └─ Suspended / Blocked ──────────► 403 Forbidden (tenant_suspended)
           │
           ▼
 2. Module Entitlement Gate     (ModuleGateMiddleware + [RequireModule("module_code")])
    └─► Tenant.ActivatedModules, plus ModuleRegistry.Requires (pharmacy requires inventory)
        └─ If not entitled ──────────────► 403 Forbidden (module_not_activated)
           │
           ▼
 3. Feature Flag Kill Switch    (same gate, TenantFeatureFlags)
    └─► Rollout / emergency off-switch per tenant + module
        └─ If disabled override exists ──► 403 Forbidden (module_disabled)
           │
           ▼
 4. Role & Permission Policy    (UseAuthorization → [AuthorizeRoles] → [RequirePermission])
    └─► Role mismatch ────────────────► 403 Forbidden (insufficient_permissions)
        Permission missing ───────────► 403 Forbidden (insufficient_permissions)
           │
           ▼
 5. Clinical-Data Scope         (controller-level rule)
    └─► Doctor reading a patient chart needs a treating relationship
        ├─ Creator or has an appointment with that doctor ──► allow
        └─ Otherwise ──► 403 Forbidden (clinical_access_denied, breakGlassRequired)
                        └─ POST /patients/{id}/break-glass writes PatientAuditLogs + AuthAuditLogs
           │
           ▼
 6. Dual-Layer Isolation Backstop (applies to every query above)
    ├─ Layer 1 (Application): EF Core global query filter (e.TenantId == CurrentTenantId)
    │    Build guard: TenantScopedInsertTests fails if an ITenantScoped entity lacks a filter.
    └─ Layer 2 (Database Engine): PostgreSQL Row-Level Security (RLS)
         via TenantConnectionInterceptor -> SET LOCAL app.current_tenant_id = '<tenant_id>'
         USING ("TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
```

**Actual middleware order** (`Program.cs`): `UseAuthentication` → `AccountSetupEnforcement`
(password + mandatory MFA enrolment) → `ImpersonationEnforcement` → `SubscriptionEnforcement` →
`ModuleGate` → `UseAuthorization` → `Idempotency` → endpoint. The module gate therefore runs
before role authorization, so a clinic that never bought pharmacy gets `module_not_activated`,
not a role error.

**No tenant filter on purpose:** `User`, `Tenant`, `TenantMembership`, `RefreshToken`,
`AuthAuditLog` are global. Login and tenant selection must resolve before a tenant is known,
so filtering or RLS on those tables would make sign-in impossible. They are whitelisted in the
build guard, not forgotten.

### 0.2 — User-to-Clinic Tenancy Model (Multi-Clinic Doctor Access)

```
┌─────────────────┐       1:N       ┌──────────────────────┐       N:1       ┌─────────────────┐
│      User       │ ─────────────── │   TenantMembership   │ ─────────────── │     Tenant      │
│ (Global Identity│                 │ (User-Tenant-Clinic  │                 │ (Clinic Business│
│  Email, Hash,   │                 │  Role, Default,      │                 │  Subscription,  │
│  MFA Secret)    │                 │  Status, ClinicId)   │                 │  Modules)       │
└─────────────────┘                 └──────────────────────┘                 └─────────────────┘
         │                                     │                                      │
         │ Authenticates credentials           │ User selects practice                │ Scopes context
         ▼                                     ▼                                      ▼
[ POST /auth/login ] ─────────► [ Multiple Memberships? ] ──YES──► Return challenge + memberships list
                                         │                              │
                                         NO                             ▼
                                         │                      [ POST /auth/select-tenant { tenantId, clinicId } ]
                                         │                              │
                                         ▼                              ▼
                              [ Issue 15-min JWT Scoped to ONE TenantId + Refresh Token Family ]
```

- **Unique key:** `(UserId, TenantId, ClinicId)` with `NULLS NOT DISTINCT`. A doctor can hold
  several branches of the same tenant; `ClinicId = NULL` means tenant-wide.
- **Refresh keeps the selected scope:** `RefreshToken` stores `TenantId`, `ClinicId` and `Role`.
  A refresh re-issues exactly that scope and re-checks the tenant subscription plus the
  membership's `active` status — it never falls back to `User.TenantId`.
- **Clinic switch = new token.** `POST /auth/select-tenant` is the only supported way to move
  tenant/clinic; the old refresh row is revoked and a new one is minted.
- **Deactivation retains data.** Revoking the commercial module or membership blocks access
  (403) but never deletes clinical rows; re-enabling restores them.

### 0.3 — Token & Session Security

- **Password hashing:** BCrypt with work factor 12 (`PasswordExtensions.DefaultWorkFactor`).
  Existing lower-cost hashes still verify; new and changed passwords get 12.
- **Login hardening:** fixed-window `auth` rate limit, per-account lockout
  (`Auth:MaxFailedAttempts` = 5, `Auth:LockoutMinutes` = 15). Every success, failure and lockout
  writes an `AuthAuditLogs` row.
- **Mandatory MFA:** `ClinicAdmin`, `Doctor`, `Receptionist` (front-desk billing) and
  `PlatformAdmin` must enrol a second factor before using the console. The token carries
  `mfa_enrolment_required`; `AccountSetupEnforcementMiddleware` allows only
  change-password/refresh/logout/MFA setup calls until it clears. Disabling MFA is rejected for
  those roles. Pharmacist and Nurse may opt in.
- **Access Token:** RS256 JWT, 15-minute lifespan. Claims: `sub`, `email`, `role`, `tenant_id`,
  `clinicId`, `mfa_enrolment_required`; header carries `kid`, and `GET /auth/.well-known/jwks.json`
  publishes the matching key so keys can be rotated without breaking verification.
- **Refresh Token Rotation & Family Revocation:**
  - Cryptographically random, stored as a deterministic SHA-256 digest (`TokenHash`, indexed).
    BCrypt is for passwords only; a per-row BCrypt scan would cap lookup at the newest 100 rows.
  - Delivered strictly via `httpOnly`, `Secure`, `SameSite=Lax` cookie. Never returned in the
    response body and never written to `localStorage`/`sessionStorage`.
  - Linked by `FamilyId`. On every refresh, the presented token is revoked and a new one issued
    in the same family, preserving the stored tenant/clinic/role.
  - **Token Theft / Reuse Detection:** If an already-revoked refresh token is presented, the system
    revokes every live token in that `FamilyId`, clears the cookie, audits
    `refresh_reuse_detected`, and returns 401 `token_theft_detected`.
- **Session Revocation (`revoke-all`):**
  - Revokes all live refresh rows and clears the cookie. The Settings UI also clears local
    consultation drafts and the billing outbox so a shared front-desk PC keeps no patient data.

### 0.4 — Module Gating, Roles & Permissions

- **Two separate switches:**
  - `Tenant.ActivatedModules` = commercial entitlement (what the clinic paid for). Parsed and
    validated by `ModuleRegistry`.
  - `TenantFeatureFlags` = rollout / emergency kill switch. It can turn a module off, never on.
- **Dependencies:** `ModuleRegistry.Requires` encodes `pharmacy → inventory`. Owning pharmacy
  without inventory is not usable, even if the pharmacy SKU was sold separately.
- **Cached, server-side:** `ModuleGateService` caches each `(tenant, module)` verdict for 30 seconds.
  Modules are deliberately not baked into the JWT, which would go stale for 15 minutes.
- **Only modular surfaces are gated:** lab, pharmacy, inventory, wishlist, precheck/emergency.
  Core flows (patients, appointments, consultations, billing) are not module-gated.
- **Hiding UI is not gating:** `[RequireModule]` runs on the server. The sidebar/route guard is
  only a courtesy redirect.
- **Permissions:** `Permissions` registry + `[RequirePermission]` layer role checks with a named
  capability so a new role does not silently inherit a money action. Applied to invoice creation
  (`invoice.write`), payment collection (`payment.record`), and consultation create/amend
  (`consultation.write`). Ledger, patient and pharmacy writes have names reserved for the same gate.
- **Clinical scope:** covered in step 5 above; break-glass is per access, reason-coded, and audited.

### 0.5 — Endpoint Map for Section 0

| Endpoint | Method | Roles | Purpose |
| :--- | :--- | :--- | :--- |
| `/api/v1/auth/login` | POST | Anonymous | Rate-limited login, lockout, MFA challenge, multi-clinic detection |
| `/api/v1/auth/select-tenant` | POST | Anonymous / Auth | Selects or switches `tenantId` + `clinicId`; mints a single-tenant token |
| `/api/v1/auth/memberships` | GET | Authenticated | Lists accessible clinics and roles for the active user |
| `/api/v1/auth/mfa/verify` | POST | Anonymous | Verifies TOTP / email code, then resolves tenant scope |
| `/api/v1/auth/mfa/setup` | POST | Authenticated | Starts enrolment and returns the TOTP secret / sends an email code |
| `/api/v1/auth/mfa/enable` | POST | Authenticated | Confirms the code, enables MFA, re-issues the session |
| `/api/v1/auth/mfa/disable` | POST | Authenticated | Password re-entry + role check; rejected for mandatory-MFA roles |
| `/api/v1/auth/mfa/status` | GET | Authenticated | Current enrolment state/method |
| `/api/v1/auth/change-password` | POST | Authenticated | Rehashes at cost 12, revokes other sessions, keeps the clinic scope |
| `/api/v1/auth/refresh` | POST | Anonymous (Cookie) | SHA-256 lookup, rotation, family revocation, scope-preserving JWT |
| `/api/v1/auth/logout` | POST | Anonymous (Cookie) | Revokes the presented refresh row and deletes the cookie |
| `/api/v1/auth/revoke-all` | POST | Authenticated | Revokes every live refresh family (shared workstation protection) |
| `/api/v1/auth/.well-known/jwks.json` | GET | Anonymous | RS256 public keys with `kid` for rotation |
| `/api/v1/users?role=` | GET | Active selected-clinic member (not PlatformAdmin) | Fails closed without matching user/tenant/clinic claims and membership; projects role and specialty from that clinic membership |
| `/api/v1/staff/me/credentials` | GET, PATCH | Doctor | Reads/updates only the caller's active selected-clinic Doctor membership |
| `/api/v1/staff/{userId}/credentials` | PATCH | ClinicAdmin, Owner | Updates an active Doctor membership in the admin's selected clinic |
| `/api/v1/patients/{id}/break-glass` | POST | ClinicAdmin, Doctor, Nurse | Reason-coded emergency override; audited in `PatientAuditLogs` + `AuthAuditLogs` |

---

## Section 1 — Reception & Patient Intake

> The front-desk intake, appointment scheduling, identity verification, and multi-specialty queue engine.

```
1. RECEPTION & PATIENT INTAKE
 • Search patient (permissioned, min length, rate-limited, audited)
     ├─ Exists ► select
     └─ New ► duplicate check (name+DOB+phone) ► create + UHID
              + Consent record (versioned notice, purposes, guardian if minor)
 • Book appointment: doctor + slot + visit type (new/follow-up)
     ├─ UNIQUE(tenant, doctor, slot) prevents double-booking
     ├─ Hashed, expiring, single-purpose precheck token
     └─ WhatsApp confirmation (only with opt-in)
 • Status: booked → confirmed → checked_in → in_nursing
           → in_consultation → completed | cancelled | no_show
 • Check-in (idempotent): per-doctor, per-day counter (clinic timezone)
     ├─ Public tracker: separate token, token no. + position only
     └─ Urgent flag (reception, structured reason) ► nurse/doctor confirms priority
```

### 1.1 — Patient Search, Identity & Family Phone Sharing (India Context)

- **Search Authorization & PHI Minimization:**
  - Strict permission check (`patients.read` / `Receptionist`, `Doctor`, `ClinicAdmin`, `Owner`, `Nurse`).
  - Nurses can browse the list, paged registry, summary, search, and demographic/consent detail. This grants no registration, demographic, consent, invoice, or consultation writes; the patient UI hides those unauthorized actions. Nursing workflows retain their existing role checks. The **Book OPD** entry remains available, but appointment creation keeps its existing server-side roles and still excludes Nurse.
  - Minimum query length: 2 characters (prevents full-database dumps).
  - Authorized collection searches are recorded in `AuthAuditLogs` (`EventType="patient_search_authorized"`), not a patient-specific `PatientAuditLogs` row.
  - Response fields are strictly minimized (ID, UHID, Name, Phone, Age/DOB, Gender) — clinical histories are hidden until patient selection.
- **Family Phone Sharing & Duplicate Resolution:**
  - In India, multiple family members routinely share a single mobile number. The system supports 1:N patients per phone while preventing accidental merges.
  - Registration runs a compound duplicate check on `(Name + DOB/Age + Phone)`.
  - If a matching phone exists with a different name or DOB, the system returns `sharedPhoneDetected: true` with existing family member previews.
  - Front-desk can confirm with `confirmDifferentPersonOnSamePhone: true` to register the new family member under their own unique record without merging.
- **Per-Clinic UHID:**
  - Auto-generated human-readable Unique Hospital Identification ID in format: `UHID-YYYYMM-XXXX` (e.g., `UHID-202610-0042`).
  - Scoped uniquely per tenant in `Patients.Uhid`.
- **Versioned DPDP Consent:**
  - Digital Personal Data Protection (DPDP) Act compliance modeled as an immutable, versioned entity (`PatientConsents`).
  - Captures: `NoticeVersion` (e.g. `v1.2`), `Language` (`en`, `hi`, `mr`), `CaptureMethod` (`reception_paper_form`, `digital_signature`, `precheck_portal`), `Purposes` (`treatment_and_records`), `CapturedAt`, `CapturedBy`.
  - For minors (< 18 years), mandatory `GuardianName` and `GuardianRelationship` (`father`, `mother`, `legal_guardian`).
  - **Consent Withdrawal:** Dedicated endpoint `POST /api/v1/patients/{id}/consents/{consentId}/withdraw`.
  - **WhatsApp Opt-in Separation:** Dedicated separate consent record (`Purpose="whatsapp_opt_in"`); messaging is strictly suppressed if not granted.

### 1.2 — Appointment Lifecycle & Strict State Machine

- **Double-Booking Prevention:**
  - Hard database-level constraint: `CREATE UNIQUE INDEX IX_Appointments_Tenant_Doctor_Date_TimeSlot ON "Appointments" ("TenantId", "DoctorId", "Date", "TimeSlot") WHERE "Status" <> 6;` (where 6 = `Cancelled`).
  - Completely eliminates race conditions from concurrent receptionist bookings across multi-specialty clinics.
- **Server-Side State Machine Transitions:**
  - Enforced in `AppointmentsController.cs`:
    - `Booked` (0) ──► `Confirmed` (1), `CheckedIn` (2), `Cancelled` (6), `NoShow` (7)
    - `Confirmed` (1) ──► `CheckedIn` (2), `Cancelled` (6), `NoShow` (7)
    - `CheckedIn` (2) ──► `InNursing` (3), `InConsultation` (4), `Cancelled` (6)
    - `InNursing` (3) ──► `InConsultation` (4), `Cancelled` (6)
    - `InConsultation` (4) ──► `Completed` (5)
    - `Completed` (5) ──► *Terminal* (no further transitions)
    - `Cancelled` (6) ──► *Terminal* (no further transitions)
    - `NoShow` (7) ──► *Terminal* (no further transitions)
  - Rescheduling transitions via `POST /api/v1/appointments/{id}/reschedule`: validates new slot availability, logs previous slot to `AppointmentHistory`, resets queue token and sets state to `Booked`.
- **Pre-Check Form Security:**
  - SHA-256 hashed precheck token stored in DB; plaintext delivered via WhatsApp/SMS to patient.
  - Scoped to one appointment, expiring at appointment slot time + 2 hours.
  - Zero PHI exposed without cryptographic token match.

### 1.3 — Atomic Per-Doctor Queue & Public Live Ticket Tracking

- **Atomic Queue Counter (Clinic Timezone):**
  - Designed for multi-specialty clinics (multiple doctors running simultaneous outpatient queues) as well as single-doctor practices.
  - Replaces vulnerable read-then-write `Max(Token) + 1` with a dedicated counter table: `DoctorDailyQueueCounter` (`TenantId`, `DoctorId`, `Date`, `LastToken`).
  - Clinic date calculated using clinic's local timezone (IST, `Asia/Kolkata` via `TimezoneService`) — avoids premature 5:30 AM queue resets from UTC dates.
  - Atomic counter increment via DB execution strategy / upsert with unique constraint on `(TenantId, DoctorId, Date)`.
- **Receptionist-Centric OPD Queue & Doctor Readiness Desk:**
  - Designed specifically for Indian hospital/clinic front-desk operations where receptionists manage lobby crowd flow and coordinate doctor room assignments.
  - **Doctor Readiness & Room Occupancy Grid:** Real-time visibility into each consulting room — displaying who is inside (`In Consult`), doctor availability (`Ready for Patient`), and the exact next patient waiting in line.
  - **Actionable Call & Hold Workflow:** Receptionists announce patients (`📢 Call to Room`), put a waiting patient on temporary hold (`⏸️ Hold / Park`) so the queue does not stall, and resume them to the waiting line (`▶️ Resume`) when they return.
- **In-Queue 1-Click Check-In & Token Issuance:**
  - Scheduled/booked (`expected`) patients can be checked in directly from the Queue view via a 1-click `📥 Check In` button, atomically issuing the sequential token (`A-01`, `A-02`) and adding them to the live lobby without switching to `/dashboard/appointments`.
- **Clinic Specialty Registration & Multi-Room TV Matrix:**
  - Clinics register their facility structure (`Clinic` for single specialty, `Polyclinic`/`Hospital` for multi-specialty) and define their offered clinical specialties.
  - **CRM Scoping:** Queue and appointment specialty filters scope to the clinic's registered disciplines. Doctor selection and EMR routing follow the doctor's own registered specialty.
  - **Waiting-Room Display (`/queue-display`):**
    - *Multi-Specialty Polyclinic:* Automatically switches to a multi-column digital room matrix displaying each consulting room, doctor, specialty badge, active "Now Serving" token, and upcoming waiting line.
    - *Single Specialty:* Displays a high-contrast single-room focal board.
- **Idempotent Check-in:**
  - Front-desk double-clicks return existing token without re-incrementing or generating duplicate queue slots.
- **Zero-PHI Public Live Tracker:**
  - Generates unguessable, cryptographically secure `QueueTrackingToken` (`qtrk_...`).
  - Public endpoint `GET /api/v1/queue-status/{trackingToken}` (rate-limited at 30 req/min per IP).
  - Exposes strictly non-identifying operational counters:
    ```json
    {
      "yourToken": 14,
      "position": 3,
      "currentlyServing": 11,
      "estimatedWaitMinutes": 30,
      "status": "waiting"
    }
    ```
  - **Zero PHI:** Patient name, phone, doctor identity, clinic specialty, and diagnosis are completely omitted.
- **Structured Emergency Triage & Liability Guardrails:**
  - Replaces free-text validation theater with standardized clinical urgency codes (`chest_pain`, `breathing_difficulty`, `severe_trauma`, `high_fever_infant`, `acute_abdomen`, `other_urgent`).
  - When flagged, the system returns an immediate **Hospital Emergency Referral Advisory** (`needsEmergencyReferral: true`), warning that the clinic is an outpatient facility and ambulance/ER transport may be required.
  - Nurse/Doctor confirms priority via clinical review, and every priority elevation is immutably logged in `PriorityLogs`.

### 1.4 — Endpoint, Entity & Component Mapping (Section 1)

| Endpoint | Method | Roles | Controller / Action | DB Entities & Constraints | Frontend Component |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `/api/v1/patients/search?q=` | GET | Receptionist, Doctor, ClinicAdmin, Owner, Nurse | `PatientsController.Search` | Audits authorization to `AuthAuditLogs` (`patient_search_authorized`) before lookup | `Patients.tsx` (Debounced query >= 2 chars) |
| `/api/v1/patients`, `/api/v1/patients/paged`, `/api/v1/patients/summary` | GET | Receptionist, Doctor, ClinicAdmin, Owner, Nurse | `PatientsController.GetAll / GetPaged / Summary` | Tenant-filtered `Patients`; `patients.read` required | `Patients.tsx`, appointment patient picker |
| `/api/v1/patients/{id}` | GET | Receptionist, Doctor, ClinicAdmin, Owner, Nurse | `PatientsController.GetById` | Tenant-filtered demographics and versioned consents; `patients.read` required | `Patients.tsx` (Patient detail) |
| `/api/v1/patients` | POST | Receptionist, ClinicAdmin | `PatientsController.Register` | `Patients` (unique `Uhid`), `PatientConsents` (versioned), `whatsapp_opt_in` | `Patients.tsx` (Registration modal + family phone check) |
| `/api/v1/patients/{id}/consents/{cId}/withdraw` | POST | Receptionist, ClinicAdmin | `PatientsController.WithdrawConsent` | `PatientConsents.WithdrawnAt` updated | `Patients.tsx` (Consent history & revocation) |
| `/api/v1/appointments` | POST | Receptionist, Doctor, ClinicAdmin | `AppointmentsController.Book` | `Appointments` (Unique `TenantId, DoctorId, Date, TimeSlot` where `Status <> 6`) | `Appointments.tsx`, `Queue.tsx` |
| `/api/v1/appointments/{id}` | PUT | Receptionist, Doctor, ClinicAdmin, Nurse (nursing/consultation only) | `AppointmentsController.Update` | Strict state machine validator (`AllowedTransitions`) | `Queue.tsx`, `Appointments.tsx` |
| `/api/v1/appointments/{id}/reschedule` | POST | Receptionist, ClinicAdmin | `AppointmentsController.Reschedule` | Validates slot uniqueness, appends to `AppointmentHistory` | `Appointments.tsx` (Reschedule modal) |
| `/api/v1/appointments/{id}/check-in` | POST | Receptionist, Doctor, ClinicAdmin | `AppointmentsController.CheckIn` | Atomic `DoctorDailyQueueCounter`, creates `QueueTrackingToken` | `Queue.tsx` (Check-In action) |
| `/api/v1/appointments/{id}/priority` | PATCH | Receptionist, Doctor, Nurse, Admin | `AppointmentsController.UpdatePriority` | `Appointments.Priority`, `TriageReasonCode`, appends `PriorityLogs` | `Queue.tsx` (Triage Modal + ER advisory) |
| `/api/v1/appointments/{id}/call` | POST | Receptionist, Doctor, Nurse, Admin | `AppointmentsController.CallPatient` | `Appointments.CalledAt`, `Appointments.CalledByUserId` | `Queue.tsx` (📢 Call to Room action) |
| `/api/v1/clinic/profile` | PUT | ClinicAdmin | `ClinicController.UpdateProfile` | `Clinics.OrganizationType`, `Clinics.Specialties` | `Settings.tsx` (Clinic Profile & Specialties) |
| `/api/v1/queue-status/{token}` | GET | Anonymous (Rate-limited) | `QueueStatusController.GetStatus` | Reads `Appointments` by `QueueTrackingToken` (Zero PHI) | `QueueStatus.tsx` (Public live tracker) |
| `/queue-display` | Page | Authenticated Staff Session | `QueueDisplay.tsx` | Multi-Room Polyclinic Grid or Single-Room focal board | `QueueDisplay.tsx` (Waiting-Room TV screen) |

---

## Section 2 — Triage/Vitals (Core) + Staff Roster (Optional Module: staff_roster)

> Core clinical triage decoupled from staff rostering. Vitals capture feeds Section 3 consultations unconditionally, while workforce scheduling, swaps, leaves, and delegation live within the optional `staff_roster` module.

```
2. TRIAGE/VITALS (core) + STAFF ROSTER (optional module: staff_roster)

 CORE: Nurse/doctor records vitals & allergies from precheck ► feeds Section 3

 MODULE (platform owner enables; clinic admin configures):
  • Shift templates ► roster (draft→published→locked), station optional
  • Rules (configurable): no double-assign, rest gap, coverage minimums
  • Swap/cover: request ► accept ► approve ► atomic apply (history kept)
  • Leave: apply ► approve chain ► roster impact ► coverage-gap alert
  • Proxies: shift substitute + time-boxed authority delegation (audited)
  • Teams: announcements + open-shift board + notifications (no free chat)
  • Shift handover: structured notes to incoming nurse
```

### 2.1 — Core Flow: Triage & Vitals Capture (Module-Independent)

- **Clinical Decoupling:**
  - Vitals review (Blood Pressure, Heart Rate, Respiratory Rate, Temperature, SpO2, Weight, Height, BMI, Blood Glucose, Allergies, Bedside Observations) is fundamentally clinical data. A clinic that does not license workforce rostering still requires vitals capture before doctor consultation.
  - Vitals endpoints are located in `VitalsController` and are **not** module-gated.
  - Authorized roles: `Nurse`, `Doctor`, `ClinicAdmin`, and `Receptionist`.
- **Automatic State Machine Advancement:**
  - Recording vitals for an appointment currently in `CheckedIn` status automatically transitions it to `InNursing`.
  - The captured vitals, allergies, and bedside observations directly populate the doctor's consultation header in Section 3 (`ConsultationsController`).

### 2.2 — Shift Assignment & Duty Roster Lifecycle

- **Shift Templates per Clinic:**
  - Configurable templates: Name (Morning, Evening, Night), Start Time, End Time, Duration, and `CrossesMidnight` flag.
  - Night shifts frequently cross midnight. Storing start time + duration in the clinic's local timezone (IST) eliminates date arithmetic bugs.
- **Roster Lifecycle States:**
  - `Draft (0)`: Schedule being assembled by clinic admin / head nurse. Modifiable without nurse notifications.
  - `Published (1)`: Finalized schedule made visible to nurses. Any subsequent modifications trigger audit logs and alerts to affected nurses.
  - `Locked (2)`: Roster period frozen for payroll and historical compliance. Further edits require explicit admin override.
- **Station & Ward Tagging:**
  - Optional `StationOrWard` field (e.g. "OPD Triage", "ICU", "Treatment Room") ensures duty assignments reflect specific clinical stations rather than vague time slots.
- **Configurable Validation Rules:**
  - **Double-assignment (Hard Block):** A nurse cannot be assigned to overlapping or duplicate shifts on the same calendar day.
  - **Rest Gap (Fixed Warning, Not a Block):** Shifts closer than 11 hours apart on adjacent days return `restGapWarnings` in the accept/approve response. The floor is a code constant (`MinimumRestHours`) and is only evaluated for shifts that carry a `ShiftTemplate` with parseable start time. Tenant-configurable rest floors and consecutive-night caps are **not implemented yet**.

### 2.3 — Shift Change & Trade Workflow

- **Structured Workflow (Not Chat Messages):**
  - Modeled as a stateful entity (`ShiftChangeRequests`): `Requested → Accepted (by Nurse B) → Approved (by Head Nurse/Admin) → Applied`.
  - Exit states: `Rejected`, `Expired`, `Cancelled`.
- **Double Re-validation:**
  - Re-validated when Nurse B accepts, and re-validated a second time when Head Nurse approves to prevent race conditions against published roster edits.
- **Two Trade Types:**
  1. **Direct Swap:** Nurse A and Nurse B trade assigned shifts.
  2. **Open-Shift Cover:** Nurse A posts an assigned shift to the open-shift board; any eligible nurse can claim it.
- **Atomic Execution & Non-Destructive History:**
  - Swaps are applied within an atomic database transaction. The original assignment is preserved in history; shift records are amended with full audit trails.

### 2.4 — Nursing Coordination vs The "Group Chat" Trap

- **Why Chat is Avoided:**
  - Free-text group chat introduces substantial surface area (moderation, retention, attachments, read receipts, search).
  - Unstructured messaging creates severe PHI leak risks (nurses pasting unredacted patient photos and lab reports into unstructured logs).
  - Healthcare staff already use WhatsApp; in-app generic chat cannot compete.
- **Structured Coordination Deliverables:**
  1. **Team Announcements Board:** Official broadcasts from Head Nurse / Clinic Admin with priority tags (`Normal` / `Urgent`).
  2. **Open-Shift Cover Board:** Transparent board for unassigned shifts and emergency coverage gaps.
  3. **Roster Change Notifications:** Direct in-app and push alerts for published schedule changes and swap decisions (zero Meta WhatsApp API per-message costs).

### 2.5 — Leave Management & Automated Coverage Gap Alerts

- **Leave Workflow:**
  - `Applied → Head Nurse / Admin Review → Approved`.
  - Supports half-day leave (`isHalfDay: true`) and withdrawal of pending requests.
- **Roster Impact Calculation:**
  - Before approval, the system evaluates the schedule and warns: *"This leave overlaps with 2 scheduled night shifts on Ward A."*
- **Automated Open-Shift Release:**
  - When leave is approved on a published roster, conflicting shifts are automatically unassigned and released directly to the **Open-Shift Cover Board** alongside an active coverage gap alert.
- **Medical File Privacy:**
  - Retains `certificateSeen: true` boolean verification flag without holding raw, sensitive medical certificate files in uncontrolled storage.

### 2.6 — Dual Proxy Architecture

- **1. Shift Proxy (Substitute Nurse):**
  - Roster-level operational substitution tied to leave or emergency absence. One nurse covers another's duty shift.
- **2. Authority Proxy (Delegated Approval):**
  - Used when Head Nurse is on leave or unavailable.
  - Strict guardrails:
    - Time-boxed with mandatory `ValidFrom` and `ValidUntil` timestamps.
    - Scoped permissions (e.g. `"roster.approve,leave.approve"`), never generic "act as this user".
    - **Self-Approval Hard Block:** Designee cannot approve their own swap or leave applications.
    - Full audit logging: every action recorded as *"approved by Designee on behalf of Delegator"*.

### 2.7 — Commercial Entitlement & Gating

- **Module Key:** `staff_roster` in `Tenant.ActivatedModules`.
- **Gating Plane:** Platform owner controls entitlement via the platform admin plane; clinic admin configures templates and rules inside the clinic console.
- **Read-Only Deactivation:** If `staff_roster` is disabled, historical rosters and swap logs remain read-only; core triage and vitals capture continue unimpeded.

### 2.8 — Endpoint, Entity & Component Mapping (Section 2)

| Endpoint | Method | Roles | Controller / Action | DB Entities & Constraints | Frontend Component |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `/api/v1/appointments/{id}/vitals` | POST | Nurse, Doctor, ClinicAdmin, Receptionist | `VitalsController.RecordVitals` | `PatientVitals`, auto-advances `Appointments.Status` to `InNursing` | `NurseDashboard.tsx` (Vitals Triage Modal) |
| `/api/v1/appointments/{id}/vitals` | GET | Nurse, Doctor, ClinicAdmin, Receptionist | `VitalsController.GetAppointmentVitals` | Reads `PatientVitals` with recorded user join | `Consultations.tsx`, `NurseDashboard.tsx` |
| `/api/v1/patients/{id}/vitals/latest` | GET | Nurse, Doctor, ClinicAdmin | `VitalsController.GetLatestPatientVitals` | Reads latest `PatientVitals` for patient EHR | `Consultations.tsx` |
| `/api/v1/staff/roster/templates` | GET, POST | ClinicAdmin, Nurse | `StaffRosterController.ListTemplates / CreateTemplate` | `ShiftTemplates` (Tenant-scoped, `CrossesMidnight`) | `NurseRoster.tsx` |
| `/api/v1/staff/roster` | GET, POST | ClinicAdmin, Nurse | `StaffRosterController.ListRosters / CreateRoster` | `StaffRosters` (`Draft`, `Published`, `Locked`) | `NurseRoster.tsx` (Lifecycle Bar) |
| `/api/v1/staff/roster/{id}/publish` | POST | ClinicAdmin | `StaffRosterController.PublishRoster` | Sets status to `Published`, audits state | `NurseRoster.tsx` |
| `/api/v1/staff/roster/{id}/lock` | POST | ClinicAdmin | `StaffRosterController.LockRoster` | Sets status to `Locked`, freezes modifications | `NurseRoster.tsx` |
| `/api/v1/staff/roster/swaps/request` | POST | Nurse, ClinicAdmin | `StaffRosterController.RequestShiftChange` | `ShiftChangeRequests` (`DirectSwap` or `OpenCover`) | `NurseRoster.tsx` (Swap Modal) |
| `/api/v1/staff/roster/swaps/{id}/accept` | POST | Nurse | `StaffRosterController.AcceptShiftChange` | Validates target nurse, moves to `Accepted` | `NurseRoster.tsx` (Swaps Tab) |
| `/api/v1/staff/roster/swaps/{id}/approve` | POST | ClinicAdmin, Nurse | `StaffRosterController.ApproveShiftChange` | Atomic DB transaction, checks AuthorityProxy | `NurseRoster.tsx` (Swaps Tab) |
| `/api/v1/staff/roster/open-shifts` | GET | ClinicAdmin, Nurse | `StaffRosterController.ListOpenShifts` | Reads unassigned or released `NurseShifts` | `NurseRoster.tsx` (Open Board) |
| `/api/v1/staff/roster/open-shifts/{id}/claim` | POST | Nurse | `StaffRosterController.ClaimOpenShift` | Creates accepted `ShiftChangeRequest` for approval | `NurseRoster.tsx` (Open Board) |
| `/api/v1/staff/roster/announcements` | GET, POST | ClinicAdmin, Nurse | `StaffRosterController.ListAnnouncements / PostAnnouncement` | `StaffAnnouncements` (`Normal` / `Urgent` priority) | `NurseRoster.tsx` (Notices Tab) |
| `/api/v1/staff/roster/leave-impact` | POST | ClinicAdmin, Nurse | `StaffRosterController.CheckLeaveImpact` | Calculates overlapping roster shifts count | `NurseRoster.tsx` (Leave Modal) |
| `/api/v1/staff/roster/proxies` | GET, POST | ClinicAdmin (create), ClinicAdmin/Nurse (list) | `StaffRosterController.ListAuthorityProxies / CreateAuthorityProxy` | `AuthorityProxies` (time-boxed, permission whitelist `roster.approve`/`leave.approve`, admin-created, parties must be clinic staff) | `NurseRoster.tsx` |
| `/api/v1/staff/roster/proxies/{id}/revoke` | POST | ClinicAdmin, delegator | `StaffRosterController.RevokeAuthorityProxy` | Sets `IsRevoked`; the proxy stops satisfying approval checks | — |
| `/api/v1/staff/roster/swaps` | GET | ClinicAdmin, Nurse | `StaffRosterController.ListShiftChanges` | Reads `ShiftChangeRequests` for the swap board | `NurseRoster.tsx` (Swaps Tab) |
| `/api/v1/nurse-care/handovers` | GET, POST | Nurse, Doctor, ClinicAdmin | `NurseCareController.ListHandovers / CreateHandover` | `ShiftHandovers` (Structured: what happened/pending) | `NurseRoster.tsx` (Handover Tab) |


---

## Section 3 — Doctor Consultation & Structured Prescriptions

> The clinical core of the EMR. Replaces fragmented notes with atomic queue claiming, structured drafts, single-transaction finalization & cryptographic signing, pediatric and allergy safety checks, immutable versioned amendments, and chemist QR verification.

```
3. DOCTOR CONSULTATION
 • Queue: claim (CheckedIn / InNursing → InConsultation); park / recall; per-doctor queue
   (the booking's doctor owns the visit — claiming never reassigns it)
 • Context panel: allergies (precheck + triage vitals), past visits, last Rx, triage vitals
 • Draft (autosave, local + server) using a specialty template
 • Diagnoses: free text + optional ICD code
 • Investigations: "advised" list (core text)  |  Lab orders (module) → lab worklist
   (no invoice line yet — billing for lab tests is not wired)
 • e-Rx: structured lines (generic, brand, strength, form, freq, duration, qty)
     ├─ Allergy + duplicate warnings; weight required for pediatrics (< 12 yrs)
     ├─ Schedule class read from the drug catalogue; NDPS blocked digitally
     └─ Copy-previous (client-side from the context panel)
 • FINALIZE (one transaction): note + Rx + orders locked ► appointment completed
     ├─ One signed record per visit (unique appointment+version); further edits = amend
     ├─ Reg. number + v2: SHA-256 content seal + verification code
     └─ Follow-up date ► optional follow-up appointment
 • Amend after finalize = new immutable version + reason (dispensed lines carried forward)
 • Lab results: versioned, doctor review required, abnormal flag
 • Claim / park / recall / draft creation / finalize / amend audited to PatientAuditLogs
```

### 3.1 — Atomic Queue Claiming & Concurrency Guardrails

- **Problem Solved:** Prevents two doctors or clinic screens from opening and consulting the same checked-in patient simultaneously.
- **Claim Endpoint:** `POST /api/v1/appointments/{id}/claim`:
  - Enforces the state check: transitions appointment to `InConsultation` **only if** current status is `CheckedIn` or `InNursing`.
  - The booking's doctor owns the visit; claiming does **not** reassign `appointment.DoctorId`. A different doctor gets `409 appointment_assigned_to_another_doctor`. `UpdatedAt` is a concurrency token, so a lost race returns `409 appointment_already_claimed_or_processed` rather than a 500.
  - If already claimed or processed, returns `409 Conflict` with `appointment_already_claimed_or_processed`.
- **Park & Recall States:**
  - `POST /api/v1/appointments/{id}/park`: Moves status from `InConsultation` to `Parked` (e.g. patient steps out for urine sample, X-ray, or outside telephone call). Treating doctor or clinic admin only.
  - `POST /api/v1/appointments/{id}/recall`: Resumes consultation, returning status to `InConsultation`.

### 3.2 — Patient Clinical Context Panel

- **Longitudinal Clinical History on One Screen:**
  - Before examining the patient or prescribing, the doctor has instant access to the patient context panel via `GET /api/v1/patients/{id}/clinical-context`:
    - **Allergies:** consolidated from the pre-check form and triage vitals, shown as high-visibility badges. (Blood group and chronic-condition lists are **not** captured on `Patient` yet — nothing is inferred or defaulted.)
    - **Triage Vitals:** BP, Heart Rate, Body Temperature (°F), Respiratory Rate, SpO2 (%), Weight (kg), Height (cm), BMI, Blood Glucose.
    - **Previous Visit Summary:** Diagnosis, treating doctor, consultation date, and prescribed medicines.
    - **One-Click Copy Previous Rx:** the client copies the last visit's medicines from this payload into today's draft; there is no server-side copy endpoint.
  - **Clinical Access Audit:** Every context fetch automatically logs a `CONSULTATION_READ` event in `PatientAuditLogs`.

### 3.3 — Draft (Non-Binding) vs. Finalize & Sign (Single Atomic Transaction)

- **Draft vs. Finalized Separation:**
  - While consulting, notes and prescription items are saved as **Drafts** (`Status = ConsultationStatus.Draft`).
  - Drafts auto-save to both local storage (sessionStorage, offline resilience against connection drops) and the server via `POST /api/v1/appointments/{id}/consultation/draft` (`prescriptionItems`, the same shape finalize uses).
  - Drafts carry **zero legal weight** and leave the appointment status as `InConsultation`. Once the visit is finalized, drafts are refused (`409 consultation_already_finalized`) — the only route is an amendment.
- **Single Atomic Transaction Finalize:**
  - `POST /api/v1/appointments/{id}/consultation/finalize`:
    1. Rejects a visit that is not `CheckedIn` / `InNursing` / `InConsultation` (`409 appointment_not_in_consultation`).
    2. Rejects a second signing of the same visit (`409 consultation_already_finalized`); the unique index `(AppointmentId, Version)` enforces the same rule under concurrency.
    3. Runs clinical safety validations (Pediatric Weight check, NDPS digital prohibition, Allergy match).
    4. Locks consultation note (`Status = ConsultationStatus.Finalized`).
    5. Attaches structured prescription items.
    6. Creates `LabOrders` for the ordered tests (no invoice line is raised — lab billing is not wired).
    7. Automatically advances appointment status to `AppointmentStatus.Completed`.
    8. Stamps Doctor's State Medical Council / NMC Registration Number from `TenantMembership.RegistrationNumber`.
    9. Issues a `v2:` SHA-256 content seal (`DigitalSignatureHash`) over the canonical signed note and medicine lines, plus a unique verification code (`VerificationQrCode`). Legacy identity-only hashes still verify as `legacy_limited`.
    10. Automatically provisions follow-up appointment if follow-up date is provided. (No reminder is queued.)
    11. Appends `CONSULTATION_FINALIZED` to `PatientAuditLogs`.

### 3.3b — Doctor Clinical Desk Ergonomics & Speed (Enhanced Workspace)

- **1-Click Nurse Triage & Precheck Import (`📥 Import into Note`):**
  - Doctors can instantly import patient precheck symptom duration, medication lists, and nurse bedside triage vitals directly into the SOAP note.
- **Common Indian OPD Quick Lab Test Panels:**
  - 1-click selectable chips for high-frequency Indian outpatient diagnostic tests: Complete Blood Count (CBC), HbA1c, Fasting Blood Sugar (FBS), Post-Prandial Blood Sugar (PPBS), Lipid Profile, Liver Function Test (LFT), Kidney Function Test (KFT/RFT), Serum Creatinine, Thyroid Profile (T3/T4/TSH), Urine Routine & Micro (C/E), Serum Electrolytes, Chest X-Ray (PA View), and 12-Lead ECG.
  - Selections automatically land in the Module 08 Lab Worklist upon consultation finalization.
- **Indian Outpatient Prescription Helpers:**
  - Quick dosage frequency chips: `1-0-1` (twice daily), `1-1-1` (thrice daily), `0-0-1` (bedtime), `1-0-0` (morning), `SOS` (as needed).
  - Quick duration chips: `3d`, `5d`, `7d`, `15d`, `30d`.
  - Quick food timings: `After food`, `Before food`, `Empty stomach`, `With food`, `At bedtime`.
- **Local Session Recovery Snapshot (`sessionStorage`):**
  - Real-time client-side draft caching keyed by appointment ID prevents loss of clinical notes during browser tab closures, reloads, or Wi-Fi disconnections.
  - Safe restoration prompt with 1-click restore or discard; automatically cleared upon finalized cryptographic sealing or amendment.
- **Facility-Scoped Clinical Disciplines:**
  - The Doctor Clinical Desk Discipline selector strictly scopes available departments and specialty toolkits (Dental, Physio, Pediatrics, Cardiology, Ortho, etc.) based on the clinic's registration configuration (`branding.specialties`).
- **Direct Ribbon Prescription Reprint (`🖨️ Print Rx`):**
  - Instant access to official signed prescription PDF directly from the patient command ribbon for finalized encounters.

### 3.4 — Structured Prescriptions & Safety Checks

- **Structured Line Items:**
  - Each item captures: `GenericName` (required), `BrandName` (optional), `DosageForm` (Tablet, Syrup, Capsule, etc.), `Strength` (e.g. 500mg), `Route` (Oral, IV, IM, Topical), `Frequency` (1-0-1, 1-0-0, 0-0-1, 1-1-1, PRN), `Duration`, `DurationUnit` (Days, Weeks), `Timing` (After food, Before food, Empty stomach, Bedtime), `TotalQuantity`, `ScheduleClass` (Schedule H, H1, G, X, NDPS), `IsUnverified` (flagged if custom drug).
  - `ScheduleClass` is authoritative from the `Drugs` catalogue whenever the line carries a `DrugId`; a client-supplied string is only used for free-text (non-catalogue) lines. `IsUnverified` marks such non-formulary lines.
- **Drug master:** `Drugs` (seeded Indian essential-medicine list) is the only formulary source; there is no favourites list and no protocol library (both remain out of scope).
- **Safety Checks:**
  - **Pediatric Prescribing Weight Requirement:** If the patient is a child (recorded `ApproxAge < 12`, else `Dob` year-difference < 12) and any prescription item is present, this visit's vitals — or anything recorded for the patient today — **must** carry a weight (`WeightKg > 0`). Otherwise `400 Bad Request` (`pediatric_weight_required`). Older visits never satisfy the check. The `Dob` comparison is year-only, so a child a month short of 12 is treated as 12.
  - **NDPS Electronic Prescribing Prohibition:** Narcotic Drugs and Psychotropic Substances are blocked digitally per drug-control rules (`400 Bad Request: ndps_digital_prescribing_prohibited`). The check reads the catalogue schedule for a linked `DrugId` first, so omitting `scheduleClass` in the request does not bypass it.
  - **Allergy Clash Warning:** Scans generic and brand names against allergens recorded on the visit's pre-check submission and on today's triage vitals (substring match, allergens of 3+ characters). Returns `warnings` with the finalize response; they do not block signing.
  - **Duplicate Drug Warning:** Flags multiple lines sharing the same generic ingredient. Also warning-only.
- **Pediatric / safety warnings are surfaced after signing** in `FinalizeSuccessModal` (it reads the `warnings` array).

### 3.5 — Immutable Amendments (FR-14 & FR-15)

- **Tamper-Evident Versioning:**
  - Once finalized, consultation records and prescriptions are immutable. A saved consultation is **never silently updated or deleted**; `AddPrescription` on a signed record is refused (`409 consultation_already_finalized`).
  - Amendments require `POST /api/v1/consultations/{id}/amend`.
  - Requires mandatory clinical rationale (`AmendmentReason`, min 5 characters — `400 amendment_reason_required`).
  - Drafts cannot be amended (`400 cannot_amend_draft`); keep editing the draft instead.
  - Creates a new record with `Version = existing.Version + 1`, `Status = ConsultationStatus.Amended`, and `PreviousVersionId` pointing to the original record.
  - Omitting `prescriptionItems` means **keep the prescription**: every prior line is cloned into the new version. Supplying a list replaces it, except lines already dispensed by pharmacy, which are always carried forward. Either way the new version carries at least one `Prescription`.
  - The new version gets its own `SignedAt` and reproducible SHA-256 signature; the `VerificationQrCode` is reused so the printed code keeps working.
  - Prior versions remain permanently viewable and auditable; `(AppointmentId, Version)` is unique at the database level.
  - `CONSULTATION_AMENDED` is appended to `PatientAuditLogs`.

### 3.6 — Public Prescription Verification for External Chemists

- **Authenticity Verification:**
  - Finalize issues a `VerificationQrCode` (e.g. `RX-1A2B3C4D5E6F7`) plus `DigitalSignatureHash`.
  - `DigitalSignatureHash` is a `v2:` SHA-256 content seal over the canonical note and medicine lines (deterministic JSON: sorted keys, normalized decimals, ordered items). Any post-signature edit to the note, diagnosis, or medicines stops matching. Older rows carrying the legacy identity-only hash verify as `legacy_limited` instead of claiming content integrity.
  - External chemists and patients can query `GET /api/v1/prescriptions/verify/{code}` (also aliased at `/api/v1/consultations/verify-rx/{code}`), anonymous, no login.
  - The response returns `signatureValid` (recomputed and compared), `version`, `amended`, clinic name/address, doctor name, registration number, qualifications, signature hash, masked patient initials, approximate age, gender, and the medicine lines — no full name, contact details, or clinical notes.
  - Amendments reuse the same code. `?version=N` returns that printed version and flags `isSuperseded` only when a newer signed version exists; the newest version stays `valid` even though an amendment leaves its row in status `Amended`. An unknown version is `404 prescription_version_not_found`.
  - An amended prescription reveals only `amendmentReason`; the superseded version's medicine lines are shown only when that exact version is requested.
  - **PDF & print:** `GET /api/v1/prescriptions/{id}/pdf` and `GET /api/v1/consultations/{id}/prescription/pdf` render a server-side PDF with an embedded vector QR code; `GET /api/v1/prescriptions/{id}/print` returns the printable payload. Every render/print appends a `PrescriptionPrintLogs` row, and reprints carry the `DUPLICATE COPY` watermark.
  - Public page `frontend/src/pages/VerifyRx.tsx` at `/rx-verify/:code` and `/verify-rx/:code`. Share links (`POST /api/v1/prescriptions/{id}/share-link`) add `exp` + `sig` (HMAC-SHA256 over `code:exp`, 7 days, `Prescription:ShareSecret`); verification rejects expired (`410`) or forged (`400`) links, and returns `503 share_link_not_configured` when the secret is unset.

### 3.7 — Lab Results Lifecycle & Review Loop

- **Lifecycle:**
  `Ordered → SampleCollected → Resulted → ReviewedByDoctor`
  (`Completed` exists on the enum but nothing sets it — `ReviewedByDoctor` is the terminal state used today.)
- **Order → Result:**
  - `POST /api/v1/consultations/{id}/lab-orders` creates an order (module `lab` entitlement required).
  - `POST /api/v1/lab-orders/{id}/collect-sample` moves `Ordered → SampleCollected`.
  - `PATCH /api/v1/lab-orders/{id}/result` records text and/or a file (25 MB cap, extension allow-list). A correction writes a **new version** with `PreviousVersionId` and preserves the original; the order flips to `Resulted` and adopts the abnormal flag.
- **Reading results:**
  - `GET /api/v1/lab-orders` is the worklist and only carries `latestResultVersion`.
  - `GET /api/v1/lab-orders/{id}` is the detail read and returns the newest `LabResult` (text, file URL, reference range, units, abnormal flag).
  - `LabOrders.tsx` lists and reviews orders; it does not yet render result text inline.
- **Doctor Review Requirement:**
  - Results flagged with `IsAbnormal = true` are visually highlighted.
  - Doctor review `POST /api/v1/lab-orders/{id}/doctor-review` stamps `DoctorReviewedAt` and `DoctorReviewedByUserId`. An order with no result is refused (`409 no_result_to_review`) — a review without something to read is a false attestation.
- **Billing:** lab orders raise **no** invoice line; there is no price field on `LabOrder`. Reception must bill separately.
- Core outpatient consultations also support a free-text "Advised Investigations" field on the consultation; formal module orders flow through `LabOrders`.

### 3.8 — Endpoint, Entity & Component Mapping (Section 3)

| Endpoint | Method | Roles | Controller / Action | DB Entities & Constraints | Frontend Component |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `/api/v1/appointments/{id}/claim` | POST | Doctor, ClinicAdmin | `ConsultationsController.ClaimAppointment` | `Appointments.Status = InConsultation`; never reassigns `DoctorId`; `409` on another doctor's visit or a lost `UpdatedAt` race; audit `APPOINTMENT_CLAIMED` | `PatientCommandRibbon.tsx` |
| `/api/v1/appointments/{id}/park` | POST | Doctor, ClinicAdmin | `ConsultationsController.ParkAppointment` | `Appointments.Status = Parked`; treating doctor or admin only; audit `APPOINTMENT_PARKED` | `PatientCommandRibbon.tsx` |
| `/api/v1/appointments/{id}/recall` | POST | Doctor, ClinicAdmin | `ConsultationsController.RecallAppointment` | `Appointments.Status = InConsultation`; treating doctor or admin only; audit `APPOINTMENT_RECALLED` | `PatientCommandRibbon.tsx` |
| `/api/v1/patients/{id}/clinical-context` | GET | Doctor, ClinicAdmin | `ConsultationsController.GetPatientContextPanel` | Reads `Patients`, `PrecheckSubmissions`, `PatientVitals`, `Consultations`; writes `PatientAuditLogs` (`CONSULTATION_READ`) | `PatientContextPanel.tsx` |
| `/api/v1/appointments/{id}/consultation/draft` | POST | Doctor | `ConsultationsController.SaveDraft` | `Consultations (Status=Draft)` + `Prescriptions`/`PrescriptionItems`; `409 appointment_closed` / `consultation_already_finalized`; audit on first save only | `Consultations.tsx` |
| `/api/v1/appointments/{id}/consultation/finalize` | POST | Doctor | `ConsultationsController.FinalizeConsultation` | One transaction: `Consultations (Status=Finalized)`, `Prescriptions`, `PrescriptionItems`, `LabOrders`, `Appointments.Status=Completed`; unique `(AppointmentId, Version)`; audit `CONSULTATION_FINALIZED` | `FinalizeSuccessModal.tsx` |
| `/api/v1/consultations/{id}/amend` | POST | Doctor | `ConsultationsController.AmendConsultation` | Creates `Consultations (v+1, Status=Amended)` with `AmendmentReason`; carries the prescription forward; audit `CONSULTATION_AMENDED` | `AmendmentModal.tsx` |
| `/api/v1/prescriptions/verify/{code}` (alias `/api/v1/consultations/verify-rx/{code}`) | GET | Anonymous (Public) | `ConsultationsController.VerifyPrescription` | Reads every signed `Consultations` row for the `VerificationQrCode`; `?version=` selects the printed copy (404 when absent); `isSuperseded` only for older versions; optional `exp`/`sig` share-link check | `VerifyRx.tsx` |
| `/api/v1/prescriptions/{id}/print` | GET | ClinicAdmin, Doctor, Receptionist, Pharmacist, Nurse | `ConsultationsController.PrintPrescription` | Appends `PrescriptionPrintLogs`; `isDuplicateCopy` + `DUPLICATE COPY` watermark on reprints | `FinalizeSuccessModal.tsx` / ribbon |
| `/api/v1/prescriptions/{id}/share-link` | POST | ClinicAdmin, Doctor, Receptionist, Pharmacist | `ConsultationsController.GenerateShareLink` | Requires active patient consent; HMAC-SHA256 `exp`+`sig`, 7 days; `503` when `Prescription:ShareSecret` is unset | `FinalizeSuccessModal.tsx` |
| `/api/v1/prescriptions/{id}/pdf` | GET | ClinicAdmin, Doctor, Receptionist, Pharmacist, Nurse (membership-scoped) | `ConsultationsController.GetPrescriptionPdf` | Renders PDF + vector QR; appends `PrescriptionPrintLogs`; reprint watermark | `FinalizeSuccessModal.tsx` |
| `/api/v1/consultations/{id}/prescription/pdf` | GET | ClinicAdmin, Doctor, Receptionist, Pharmacist, Nurse (membership-scoped) | `ConsultationsController.GetConsultationPrescriptionPdf` | Resolves the consultation's prescription and delegates to the PDF renderer | `FinalizeSuccessModal.tsx` |
| `/api/v1/consultations/{id}/lab-orders` | POST | Doctor | `LabOrdersController.CreateOrder` | `LabOrders (Status=Ordered)`; module `lab`; no invoice line | none (the SPA orders tests through finalize's `orderedLabTests`) |
| `/api/v1/lab-orders` | GET | Doctor, ClinicAdmin | `LabOrdersController.Worklist` | `LabOrders` + `latestResultVersion` | `LabOrders.tsx` |
| `/api/v1/lab-orders/{id}` | GET | Doctor, ClinicAdmin, Nurse | `LabOrdersController.GetOrder` | `LabOrders` + newest `LabResults` version | none (API only) |
| `/api/v1/lab-orders/{id}/collect-sample` | POST | Nurse, ClinicAdmin | `LabOrdersController.CollectSample` | `LabOrders.Status = SampleCollected` | none (API only) |
| `/api/v1/lab-orders/{id}/result` | PATCH | Doctor, ClinicAdmin | `LabOrdersController.EnterResult` | New `LabResults` version (`Version`, `PreviousVersionId`); `LabOrders.Status = Resulted` | none (API only) |
| `/api/v1/lab-orders/{id}/doctor-review` | POST | Doctor | `LabOrdersController.DoctorReview` | `LabOrders.Status = ReviewedByDoctor`, `DoctorReviewedAt`; `409 no_result_to_review` | none (API only) |

**Section 3 known gaps (documented, not built):** lab tests are not billed; no drug favourites or protocol library; `ConsultTemplate` has no version column (so `Consultation.TemplateVersion` is client-reported); `LabOrderStatus.Completed` is unreachable; receptionist can still read clinical endpoints. Prescription PDF, the vector QR image, the lab-test picker, and the public verification page are now built.

---

## Section 5 — Billing, Payments & Append-Only Financial Ledger (Core Module)

> **Core Engine (Non-Gated).** Manual-first billing engine. Invoices carry per-line tax determined by the tenant's tax profile (enforcing GST exemptions for clinical healthcare under Notification 12/2017). Direct patient-to-clinic bank payment capture (dynamic UPI QR with backup account toggle, card slip, cash, NEFT/RTGS). Staff verification in bank app/soundbox transitions invoices to paid. All financial state transitions post to an append-only ledger (`REVOKE UPDATE, DELETE FROM app_user`). Automation (statement import, payment links, live PSP routing) plugs behind the exact same states later.

```
5. BILLING, PAYMENTS & LEDGER  (core module, not gated)
[InvoicesController, PaymentsController, PayeeAccountsController,
 LedgerController, ReconciliationController]  (proposed names)

5.1 INVOICE BUILD   POST /api/v1/invoices   (idempotency key optional, recommended)
  • Sources: finalized consultation (Sec 3) → consultation fee, procedures,
    lab orders (if lab module). Pharmacy sales (Sec 4A) are NOT merged:
    they keep their own invoice series (PHARM-YYYY-XXXXXX) and appear as a linked document.
  • Each line: description, qty, rate, tax category (exempt | nil | taxable),
    HSN/SAC, GST rate. Defaults come from the tenant tax profile; a client may
    raise tax but cannot zero-rate a taxable line (exempt needs a 9993xx SAC).
  • Invoice status: draft (editable) → finalized (immutable)

5.2 TENANT TAX PROFILE (set at onboarding, confirmed by the clinic's CA)
  • GST registered? GSTIN, state, invoice type (tax invoice vs bill of supply)
  • Default tax category per service type; no hardcoded flat rate

5.3 ADJUSTMENTS
  • Discount: reason required, rejected above gross, posted as a contra-revenue
    line; the clinic-set % approval threshold is not built
  • Advance/deposit: built (PatientDeposits + PatientDepositAllocations; collected,
    applied to an invoice, refunded; each posts to the ledger)
  • Packages and memberships: later

5.4 FINALIZE
  • Sequential, gapless number per tenant + year + series (INV-2026-27-000142;
    the FY segment is "2026-27" from IndianFinancialYearUtils), MAX+1 in a
    serializable transaction with a DB unique index
  • Server-side printable payload from the finalized version only; reprints marked
    duplicate. No PDF renderer — the endpoint returns JSON for the UI to render
  • Mistakes → credit note + new invoice, never an edit

5.5 PAYMENT CAPTURE  (manual-first; no platform escrow - money settles to the
 clinic's own bank account, or the clinic's own Razorpay merchant account)
  • Methods: cash, card (slip ref), UPI QR, bank transfer
  • UPI: reception taps "Collect via UPI" → dynamic UPI QR (invoice amount +
    invoice ref) from the primary payee account; "Use backup account" button
  • Static clinic QR = secondary: patient types amount; reception logs a
    manual payment and confirms it the same way
  • Partial and split payments allowed; each attempt has its own reference

5.6 CONFIRMATION
  • Optional "reported" marker (patient says paid): a hint, never proof
  • Staff check the credit in the bank app/soundbox → Confirm (+UTR,
    mandatory at or above a clinic-set amount) → invoice paid / partially paid
  • A second confirm of the same payment and an over-balance amount are rejected
  • Cap on reported-but-unconfirmed payments (count and amount) and the
    staff-confirmation spike alert are enforced from TenantTaxProfile
    (MaxUnconfirmedReportedPaymentsCount/Amount, StaffConfirmationSpikeThreshold)
  • Receipt: printed, or WhatsApp with patient opt-in

5.7 PAYEE ACCOUNT CONTROLS
  • Add/change: ClinicAdmin or Owner, password + MFA code step-up, 24-hour
    cooling-off, email alert to every clinic admin
  • Accounts are clinic-scoped: the QR only offers the invoice clinic's own
    account, never a sibling clinic's
  • Priority order stored per clinic; daily limit enforced when the dynamic QR
    picks an account (over-limit accounts are skipped; 400
    payee_daily_limit_exceeded when none has headroom)

5.8 REFUNDS & CREDIT NOTES
  • Dual control; refund sent from the clinic's bank manually, ref recorded
  • Always a reversal entry, never a delete

5.9 LEDGER & RECONCILIATION
  • Append-only (app_user: REVOKE UPDATE, DELETE); entries for finalize,
    payment confirmed, refund, credit note; corrections are reversals
  • End-of-day summary: confirmed vs reported vs bank credits (manual compare)
  • Reports: daily collection by method, outstanding dues, aging

5.10 DEFERRED (same states, plug in later)
  • v1.5 bank statement import → auto-match on UTR + amount + time
  • v2 WhatsApp payment link (needs redirect page + opt-in + WhatsApp costs)
  • v3 PSP adapters (Razorpay etc.), account routing from real outcome data
  • Later: insurance/TPA, e-invoicing thresholds, packages
```

### 5.1 — Regulatory & Tax Exemption Engine (Notification 12/2017)
- **Healthcare Services Exemption:**
  - Under Notification 12/2017 Central Tax (Rate), health care services by a clinical establishment or authorised medical practitioner are **exempt / nil-rated (0% GST)** (SAC `999312`). OPD consultations, doctor fees, and nursing procedures are nil-rated.
  - A healthcare provider making exclusively exempt supplies is **not required to register for GST** under Section 23 of the CGST Act.
  - Room rent exceeding ₹5,000/day (outside exemption) is taxable if nursing home or IPD features are added.
- **Goods & Pharmacy Separation (Section 4A vs Section 5):**
  - Over-the-counter medicine sales and pharmacy goods are taxable items at item-specific rates (mostly 5% or 12%).
  - **Inviolable rule:** In-house pharmacy sales (Section 4A) are **NOT merged** into clinical service invoices. Pharmacy generates its own independent invoice series (`PHARM-YYYY-XXXXXX`) with reverse-calculated MRP-inclusive GST and appears only as a linked document on patient accounts.
- **Invoice Type (Tax Invoice vs Bill of Supply):**
  - Exempt-only clinics issue a **Bill of Supply** (no tax charged, no input tax credit).
  - Mixed clinics (taxable goods + exempt healthcare) issue a **Tax Invoice** for taxable lines and/or a composite Bill of Supply, configured via the clinic's **Tenant Tax Profile**.

### 5.2 — Invoice Lifecycle & Line-Item Build
- **Draft Status (Editable):**
  - Items populated from finalized consultation (consultation fee, procedures, diagnostic tests).
  - Front-desk can adjust quantities or attach additional outpatient procedure lines.
- **Line Structure:**
  - `Description`: Item or service name.
  - `Quantity`: Unit quantity.
  - `UnitPrice`: Unit rate before tax.
  - `TaxCategory`: `Exempt` | `Nil` | `Taxable`.
  - `HsnSacCode`: SAC `999312` (OPD services) or HSN code.
  - `GstRate`: Percentage (0% for exempt healthcare, 5%/12%/18% for taxable goods).
  - `TaxAmount`: Calculated per line item (`Quantity * UnitPrice * (GstRate / 100)`).
  - `TotalAmount`: `Quantity * UnitPrice + TaxAmount`.
- **Finalized Status (Immutable):**
  - Assigns a sequential, gapless numbering scheme: `INV-{YYYY}-{6-digit sequence}` (e.g. `INV-2026-000142`), unique per tenant/series/year and backed by a DB unique index; the sequence is `MAX+1` inside a serializable transaction.
  - Numbering uses the calendar year, not the Indian FY (Apr–Mar) yet — CA sign-off item.
  - Once finalized, line items and totals are strictly locked.
  - Corrections must be issued via **Credit Notes** and new invoices, never by mutating finalized records.
  - Printable receipt payload generated server-side; only finalized invoices can be printed, and reprints increment print counters and render a `"DUPLICATE COPY"` watermark. The endpoint returns JSON; there is no PDF renderer.

### 5.3 — Adjustments: Discounts & Patient Deposits
- **Discounts:**
  - Displayed as a separate deduction line (`DiscountAmount`), never by altering baseline rates.
  - Requires a mandatory reason text; the server rejects a discount above the gross and posts it as a `DiscountAllowed` contra-revenue debit so the ledger still balances.
  - Clinic-configurable discount threshold (e.g., discounts > 15% require Owner / ClinicAdmin approval) is **not built** — there is no threshold setting or approval step.
- **Patient Deposits / Advances:**
  - Built: `PatientDeposits` + `PatientDepositAllocations` (`PatientDepositsController`). A deposit is collected (cash/UPI/card/netbanking, receipt number), applied to one or more invoices, or refunded; each step posts an append-only ledger entry (`DepositCollected` / `DepositApplied` / `DepositRefunded`).

### 5.4 — Manual-First Direct Payment Capture
- **Direct Clinic Bank Account Routing:**
  - Cash / card / UPI / bank-transfer collections settle straight into the **clinic's own bank account** — no platform escrow, no pooled wallet, no aggregator balance held by Samstack.
  - A clinic may also enable its **own** Razorpay merchant account (`PaymentMethod.Razorpay`); receipts are then verified by webhook signature and reconciled through the unmatched-receipt queue before matching an invoice. Razorpay is a PSP in that path, so "no intermediary at all" applies only to the manual methods.
- **Payment Methods:**
  - `Cash`: Cash collected at reception desk.
  - `Card`: Terminal POS swipe, staff records terminal slip transaction reference.
  - `UPI QR`: Dynamic UPI QR rendered on reception screen using standard NPCI UPI link specification:
    `upi://pay?pa={VPA}&pn={ClinicName}&am={Amount}&tr={InvoiceRef}&cu=INR&tn=Invoice+{InvoiceNum}`
    - Displays primary payee VPA with instant `"Use Backup Account"` button for bank downtime resilience.
    - Static clinic QR remains secondary (patient types amount manually).
  - `BankTransfer`: Direct NEFT / RTGS / IMPS credit.
- **Split & Partial Payments:**
  - Supports split payment tenders (e.g. ₹500 Cash + ₹700 UPI for a ₹1,200 invoice).
  - Each tender creates its own payment transaction reference.

### 5.5 — Verification & Staff Confirmation
- **"Reported" State (Hint Only):**
  - When a patient claims payment (showing phone screen), staff or patient can mark payment as `Reported`. This is treated strictly as an operational hint and **never** marks the invoice paid. The reported amount is capped at the balance due and an unknown tender is rejected.
  - Hard anti-fraud cap on total reported-but-unconfirmed amounts and spike detection alerts are **not built** (no clinic threshold setting or alert worker).
- **"Confirmed" State (Financial Truth):**
  - Reception staff verifies actual credit in clinic bank mobile application or soundbox announcement.
  - Staff clicks "Confirm Payment" and enters mandatory Bank UTR / Reference Number (mandatory at or above clinic-set threshold, e.g. ₹2,000). A second confirm of the same payment, or an amount above the balance due, is rejected; an idempotency key makes retries safe.
  - Payment moves to `Completed`; invoice status updates to `Paid` (or `PartiallyPaid`).
  - Receipt delivered via printout or WhatsApp (only with DPDP opt-in).

### 5.6 — Payee Account Controls & Security
- **Owner / ClinicAdmin Management:**
  - Modifying or adding payee bank VPAs/accounts requires the `ClinicAdmin` or `Owner` role plus a password re-entry and, when the user has MFA enrolled, a current authenticator/email code.
  - **24-Hour Cooling-Off Period:** Newly added or re-activated payee accounts are placed in cooling-off status before receiving live transactions.
  - Security alert email is sent to every clinic admin on add/update.
  - Daily transaction limit is stored per payee account but **not enforced**; payments do not yet record which payee account received them, so a truthful daily total cannot be computed.

### 5.7 — Dual-Control Refunds & Credit Notes
- **Credit Notes:**
  - Issued only against a finalized (or paid/partially paid) invoice for billing errors, cancelled procedures, or rate adjustments.
  - Carries its own sequential credit note series (`CN-{FY}-{Sequence}`, Indian FY) and a mandatory reason; cumulative *approved* notes on one invoice cannot exceed the invoice total.
  - **Dual control:** at or above `TenantTaxProfile.ThresholdCreditNoteApproval` (default ₹5,000) the note is created `PendingApproval`. The ledger reversal and the balance reduction happen only when a *different* `ClinicAdmin`/`Owner` approves it; the issuer cannot approve or reject their own note. Below the threshold the single-approver path is kept.
- **Refunds (`POST /invoices/{id}/refund`):**
  - Initiator records the amount, reason and a mandatory refund reference (UTR, gateway refund ID, or voucher).
  - At or above `TenantTaxProfile.ThresholdRefundApproval` (default ₹1,000) the refund is `PendingApproval` until a different admin approves it; the `RefundProcessed` ledger pair posts only on approval.

### 5.8 — Append-Only Ledger & Reconciliation
- **Database Role Enforcement:**
  - Enforced at PostgreSQL role level: `REVOKE UPDATE, DELETE ON TABLE "FinanceLedgerEntries" FROM app_user` (also `InvoicePrintLogs` and `CreditNotes`), plus an EF change-tracker guard.
  - Financial ledger entries are posted automatically on **Invoice Finalize**, **Payment Confirmed** (manual confirm, legacy collect, and Razorpay webhook), **Credit Note Approved**, and **Refund Approved**.
  - Corrections must be recorded as explicit offsetting reversal entries.
- **Reconciliation & Reporting:**
  - End-of-day summary comparing confirmed payments vs reported hints vs bank credits; reported hints are counted by `ReportedAt`.
  - Financial reports: Daily collection by payment tender, outstanding patient dues, aging accounts receivable. GST is reported pro-rata to the amount collected in the period, not the full invoice GST per payment.
  - Bank-credit comparison is manual; statement import is deferred to v1.5.

### 5.9 — Deferred Automation Roadmap
- **v1.5:** Bank statement CSV/MT940 import with automated 3-way matching on UTR + amount + timestamp.
- **v2.0:** WhatsApp payment link generation with branded clinic redirect page and DPDP opt-in messaging.
- **v3.0:** Live PSP adapters (Razorpay, Pine Labs) with dynamic transaction routing based on bank success rates.
- **Track 4+:** Insurance / TPA cashless claim processing and GST e-invoicing API integration.

### 5.10 — Pre-Implementation Verification Checklist
1. **Chartered Accountant (CA):** Confirm Bill of Supply vs Tax Invoice classification for exempt-only vs mixed clinics, verify HSN/SAC codes, invoice sequence continuity rules, and pharmacy tax registration separation.
2. **Legal Counsel:** Verify that generating clinic-direct dynamic UPI QRs remains purely technical communications and does not trigger RBI Payment Aggregator / Payment Gateway (PA/PG) guidelines.
3. **NPCI / Bank:** Validate UPI merchant category codes (MCC 8062/8099 for healthcare) and merchant transaction fee applicability.
4. **Device Testing:** End-to-end QR scan and deep link verification across GPay, PhonePe, Paytm, and BHIM apps.

### 5.11 — Priority Execution Matrix
- **Must do now:** 5.1 (Invoice Build), 5.2 (Tenant Tax Profile), 5.4 (Finalize & Sequential Numbering), 5.5 (Payment Capture & Dynamic QR), 5.6 (Staff Confirmation & UTR), 5.8 (Refunds & Credit Notes), and core of 5.9 (Append-Only Ledger).
- **Should do soon:** 5.3 (discount approval threshold only — deposits are built), 5.7 (Payee Account Cooling-Off & Alerts), Bank Statement Import (v1.5).
- **Can wait:** WhatsApp Payment Links (v2), PSP Routing Adapters (v3), Insurance/TPA Packages.
- **Strictly do not do:** Reading customer SMS/messages, pooling or settling funds (escrow/aggregator risk), or promising "zero fees" in sales copy.

---

## Phase 1 Workflow (FR-01 to FR-22)

### 1.1 — Identity & Access

```
┌──────────────────────────────────────────────────────────────┐
│ FR-01: User Login                                            │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/auth/login { email, password }
       │
       ▼
  Rate limit + per-account lockout (5 fails / 15 min)
       │
       ▼
  BCrypt verify (cost 12) ──► MFA challenge if enrolled; enrolment gate if the
       │                      role requires a second factor (see Section 0.3)
       ▼
  Generate JWT (RS256)
       │              ├─ kid from persistent key (PemFileKeyService
       │              │   for dev, AzureKeyVaultKeyService for prod
       │              │   — ADR-07)
       │              ├─ 15-min access token
       │              └─ 7-day refresh token (rotated, httpOnly cookie only)
       ▼
  InactivityMiddleware (30-min sliding window for Doctor/Admin,
  in-memory cache, 2-min TTL)
       │
       ▼
  GET /api/v1/auth/.well-known/jwks.json
       └─► Exposes public key for downstream verification

┌──────────────────────────────────────────────────────────────┐
│ FR-02: Role Enforcement (server-side)                       │
└──────────────────────────────────────────────────────────────┘
  [AuthorizeRoles("Doctor", "ClinicAdmin", "Receptionist")]
       │
       ▼
  RbacHandler ──► reads "role" claim from JWT
       │
       ▼
  [Authorize] short-circuit on mismatch

┌──────────────────────────────────────────────────────────────┐
│ FR-03: Password Reset                                        │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/auth/request-password-reset { email }
       └─► EmailPasswordResetToken (30-min expiry)
  POST /api/v1/auth/confirm-password-reset { token, newPassword }
       └─► Revokes all existing sessions for that user

┌──────────────────────────────────────────────────────────────┐
│ FR-04: Staff Invitation                                      │
└──────────────────────────────────────────────────────────────┘
  Clinic Admin ──► POST /api/v1/staff/invite { name, email, role }
       └─► StaffInvite row (72h expiry, single-use)
  Invitee ──► POST /api/v1/staff/accept-invite { token, password }
       └─► User row + TenantMembership (tenant, clinic, role) + role_granted audit
```

**Selected-clinic directory and signing credentials:**
- `GET /api/v1/users?role=doctor` uses the caller's authenticated identity, matching tenant/clinic claims and database tenant scope, and an active membership with the token's role. Missing or mismatched scope returns `403`; a missing identity returns `401`. The directory excludes PlatformAdmin, filters/projects the selected membership's role and specialty, and returns `400 invalid_role` for an unknown or undefined role filter instead of returning all staff.
- As a Doctor, open **Settings → Signing credentials** to read or update your own selected-clinic membership using `GET`/`PATCH /api/v1/staff/me/credentials`. ClinicAdmin/Owner retains `PATCH /api/v1/staff/{userId}/credentials` for an active doctor in the same selected clinic.
- Both PATCH routes require `{ registrationNumber, qualifications }`, trim surrounding spaces, and validate 2–100 / 2–200 characters respectively. They return the saved fields and persist the membership plus `SigningCredentialsUpdated` audit in one save. Only future signatures use the updated credentials; existing consultation snapshots and signature hashes stay unchanged. The form loads independently of clinic configuration; session/clinic changes cancel outgoing requests and ignore stale responses so they cannot populate the new form.

### 1.2 — Clinic Configuration

```
┌──────────────────────────────────────────────────────────────┐
│ FR-05: Clinic Profile Setup                                  │
└──────────────────────────────────────────────────────────────┘
  GET /api/v1/clinic/profile ──► name, hours, holidays, special hours,
                                  GST rate, token format, branding, etc.
  PUT /api/v1/clinic/profile ──► update 30+ configurable fields
  PUT /api/v1/clinic/hours ──► per-day open/close (all 7 days)
  POST /api/v1/clinic/holidays ──► exact dates or recurring annual
  DELETE /api/v1/clinic/holidays/{id}
  POST /api/v1/clinic/special-hours ──► date-specific overrides
  DELETE /api/v1/clinic/special-hours/{id}
       │
       ▼
  Booking validates: holiday → special hours → weekly schedule
  (precedence: most-specific wins)
```

### 1.3 — Patient Module

```
┌──────────────────────────────────────────────────────────────┐
│ FR-06: Register Patient (Multi-Specialty & Family Phone Aware) │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/patients { name, phone, dob|approxAge, gender,
                            address, consent: { noticeVersion, language,
                            captureMethod, guardianName, guardianRelationship },
                            whatsAppOptIn: bool,
                            confirmDifferentPersonOnSamePhone: bool,
                            idempotencyKey? }
       │
       ├─► Compound duplicate check (name + DOB + phone)
       ├─► Family phone sharing: returns sharedPhoneDetected warning unless confirmed
       ├─► Idempotency-key replay protection
       ├─► Generates human-readable UHID (UHID-YYYYMM-XXXX)
       └─► Creates: Patient row + versioned PatientConsent row + WhatsApp opt-in record

┌──────────────────────────────────────────────────────────────┐
│ FR-07: Search / View Patient (Permissioned & Audited)        │
└──────────────────────────────────────────────────────────────┘
  GET /api/v1/patients/search?q=<min 2 chars>
       ├─► Checked against 'patients.read' role permission
       ├─► Minimum 2 characters required (prevents DB dumping)
       ├─► Records search authorization in AuthAuditLogs (patient_search_authorized)
       └─► Returns minimized PHI (Id, Uhid, Name, Phone, Age, Gender)
  GET /api/v1/patients/{id} ──► full demographics + versioned consents
  PATCH /api/v1/patients/{id} ──► update demographics

┌──────────────────────────────────────────────────────────────┐
│ FR-08: Edit Patient (append-only audit)                      │
└──────────────────────────────────────────────────────────────┘
  PATCH triggers PatientAuditLog row (never UPDATE/DELETE —
  enforced at DB role level: REVOKE UPDATE,DELETE on
  PatientAuditLogs FROM app_user)

┌──────────────────────────────────────────────────────────────┐
│ FR-09: DPDP Versioned Consent & Withdrawal                   │
└──────────────────────────────────────────────────────────────┘
  Immutable versioned entity in PatientConsents with NoticeVersion,
  Language, CaptureMethod, Purposes, Guardian details for minors.
  POST /api/v1/patients/{id}/consents/{consentId}/withdraw ──►
  records WithdrawnAt timestamp. WhatsApp opt-in kept separate.
```

### 1.4 — Appointments

```
┌──────────────────────────────────────────────────────────────┐
│ FR-10: Book Appointment (Double-Booking Protected)           │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/appointments { patientId, doctorId, date, timeSlot,
                                type: "scheduled|walkin" }
       │
       ▼
  Serializable transaction + DB Unique Constraint:
  1. UNIQUE(tenant_id, doctor_id, date, time_slot) WHERE Status <> 6 (Cancelled)
     prevents concurrent double-booking in multi-specialty clinics.
  2. Outside working hours / holiday? ──► 400
  3. Create Appointment row (Status=Booked)
  4. [Phase 2: MOD-23] Generate SHA-256 hashed precheck token
  5. Commit
  6. Fire async:
     - WhatsApp confirmation (dispatched ONLY if patient has active 'whatsapp_opt_in' consent)
     - [Phase 2: MOD-13] rule evaluation
       │
       ▼
  Returns 201 { appointmentId, status: "booked", queueToken: null }

┌──────────────────────────────────────────────────────────────┐
│ FR-11: View Daily Schedule & State Machine                   │
└──────────────────────────────────────────────────────────────┘
  GET /api/v1/appointments?date=&doctorId=&status=
       └─► [Phase 2: MOD-24] Sort: emergency DESC, date ASC, time ASC
       └─► Polling refresh (HTTP, no WebSockets per TRD)
  Strict server-side state transitions enforced on PUT /api/v1/appointments/{id}:
       booked ──► confirmed ──► checked_in ──► in_nursing ──► in_consultation ──► completed
       Exit transitions: cancelled, no_show

┌──────────────────────────────────────────────────────────────┐
│ FR-12: Generate Queue Token (Atomic Per-Doctor & Zero-PHI)   │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/appointments/{id}/check-in
       │
       ▼
  1. Idempotent: returns existing token if already checked in
  2. Compute date in clinic timezone (Asia/Kolkata / IST)
  3. Atomic counter increment in DoctorDailyQueueCounter
     (per-tenant, per-doctor, per-day)
  4. Generates unguessable QueueTrackingToken (qtrk_...)
  5. Status becomes 'checked_in'
       │
       ▼
  Returns 200 { status: "checked_in", queueToken: N, trackingToken: "qtrk_..." }

┌──────────────────────────────────────────────────────────────┐
│ FR-13: Reschedule / Cancel                                  │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/appointments/{id}/reschedule { newDate, newTimeSlot }
       └─► Verifies slot uniqueness, appends to AppointmentHistory,
           resets queueToken & status back to Booked
  PUT /api/v1/appointments/{id} { status: "cancelled" }
       └─► Validates terminal exit transition, frees slot in index
```

### 1.5 — Clinical

```
┌──────────────────────────────────────────────────────────────┐
│ FR-14: Create Consultation (versioned amendments)            │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/appointments/{id}/consultation
       { chiefComplaint, observations, diagnosis, previousVersionId? }
       │
       ▼
  If previousVersionId set, version = prev.version + 1
  Otherwise version = 1
       │
       ▼
  POST /api/v1/consultations/{id}/amend ──► creates new Consultation
       with PreviousVersionId pointing to original
       (amendment, never silent overwrite)

┌──────────────────────────────────────────────────────────────┐
│ FR-15: Write Prescription                                    │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/consultations/{id}/prescriptions
       { items: [{ medicine, dosage, duration, frequency }] }
       └─► Prescriptions + PrescriptionItems linked to consultation

┌──────────────────────────────────────────────────────────────┐
│ FR-16: Treatment Timeline                                    │
└──────────────────────────────────────────────────────────────┘
  GET /api/v1/consultations?patientId=&dateFrom=&dateTo=
       └─► Chronological consultations + prescriptions per patient
```

### 1.6 — Billing

```
┌──────────────────────────────────────────────────────────────┐
│ FR-17: Generate Invoice                                      │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/invoices { appointmentId, lineItems: [...] }
       │
       ▼
  Serialized transaction:
  1. Next invoice number (gapless) — uses ExecutionStrategy
  2. GST = subtotal × DefaultGstRate
  3. Total = subtotal + GST
  4. Invoice row + InvoiceLineItem rows

┌──────────────────────────────────────────────────────────────┐
│ FR-18: Collect Payment                                       │
└──────────────────────────────────────────────────────────────┘
  POST /api/v1/invoices/{id}/payment
       { method: "cash|razorpay", amount }
       │
       ├─► Cash: marks Payment Completed + Invoice Paid immediately
       │
       └─► Razorpay:
           1. Create payment link (Razorpay SDK stub)
           2. Return { paymentLinkUrl, paymentId }
           3. Patient pays on Razorpay
           4. Webhook hits POST /api/v1/invoices/webhooks/razorpay
              - HMAC SHA256 verify X-Razorpay-Signature
                (constant-time compare)
              - Mark Payment Completed, Invoice Paid
           5. [Phase 1 carry-over] RazorpayReconciliationWorker
              polls pending payments every 60 min as safety net

┌──────────────────────────────────────────────────────────────┐
│ FR-19: View Outstanding Dues                                 │
└──────────────────────────────────────────────────────────────┘
  GET /api/v1/invoices?status=unpaid
       └─► Admin: all unpaid; Doctor: own patients only
```

### 1.7 — Notifications (Phase 1 → Phase 2 migration)

```
┌──────────────────────────────────────────────────────────────┐
│ FR-20/21 → MOD-13 defaults (see Phase 2 §A.3)               │
└──────────────────────────────────────────────────────────────┘
  Phase 1 path: booking flow fires SendAppointmentConfirmationAsync
  and ReminderSchedulerService (24h-before daily batch).
  Phase 2: these continue to work; Clinic Admin can configure
  additional rules via MOD-13.
```

### 1.8 — Offline Sync (Deferred)

```
┌──────────────────────────────────────────────────────────────┐
│ FR-22: Offline-Tolerant Sync                                 │
└──────────────────────────────────────────────────────────────┘
  STATUS: Not built. No IndexedDB queue in frontend yet.
  Server-side idempotency keys are in place (FR-06/17) — the
  client-side half is deferred per state/technical-debt.md item 7.
```

---

## Phase 2 Workflow (9 Modules)

### 2.A — Patient Pre-Visit

```
┌──────────────────────────────────────────────────────────────┐
│ MOD-23: Pre-Check Form (FR-23-01/02/03)                      │
└──────────────────────────────────────────────────────────────┘
  [FR-23-01] Booking transaction (FR-10) generates pre-check:
     - SHA256-hashed token (plaintext sent in WhatsApp, hash stored)
     - Skip if walk-in OR <2h lead time
     - Token expiry = appointment slot time
     - WhatsApp message: "...appointment confirmed. Pre-visit form: {link}"

  [FR-23-02] Patient clicks link (unauthenticated):
     POST /api/v1/precheck/{token}
     { chiefComplaint?, symptomDuration?, medications?, allergies? }
     └─► Updates PrecheckSubmission row (sets SubmittedAt)

  [FR-23-03] Doctor in consult screen:
     GET /api/v1/appointments/{id}/precheck
     └─► Returns all four fields inline (chiefComplaint/symptomDuration/
         medications/allergies) for review

  Edge case: correction = doctor edits in their own consultation
  notes (no second precheck submission). The form is one-shot per
  appointment.

┌──────────────────────────────────────────────────────────────┐
│ MOD-24: Emergency Queue & Structured Triage (FR-24-01/02)    │
└──────────────────────────────────────────────────────────────┘
  [FR-24-01] Receptionist/Doctor/Nurse/ClinicAdmin:
     PATCH /api/v1/appointments/{id}/priority
     { priority: "emergency|normal", triageReasonCode: "chest_pain|breathing_difficulty|..." }
       │
       ▼
     - Validates structured urgency code from allowed list
     - Updates Appointment.Priority, TriageReasonCode, TriageConfirmedBy
     - If severe symptom: returns immediate Hospital ER Referral Advisory
       (needsEmergencyReferral: true, "Consider calling ambulance / immediate hospital referral")
     - Appends immutable PriorityLog row (who, when, reason, what)
     - Idempotent (no DB write if priority & code unchanged)

  [FR-24-02] GET /api/v1/appointments?date=... automatically
     OrderByDescending(Priority) → ThenBy(Date) → ThenBy(TimeSlot)
     (Emergency surfaces next without disturbing underlying slot times)

  Edge case: two emergencies flagged in sequence served in flag order,
  not re-litigated. Clinician review confirms urgency.

┌──────────────────────────────────────────────────────────────┐
│ MOD-25: Live Ticket Tracking (Zero-PHI) (FR-25-01)          │
└──────────────────────────────────────────────────────────────┘
  Receptionist checks in patient (FR-12) ──► atomic queueToken & trackingToken assigned
       │
       ▼
  Link format: http://localhost:5173/queue/{trackingToken}
  (Opaque 128-bit random token 'qtrk_...', not guessable sequential ID)
       │
       ▼
  Patient (unauthenticated, rate-limited at 30 req/min):
     GET /api/v1/queue-status/{trackingToken}
     └─► { yourToken: int, currentlyServing: int, position: int,
           estimatedWaitMinutes: int, status: string }
     - STRICTLY ZERO PHI: no patient name, no doctor name, no specialty, no diagnosis
     - Filtered by doctor + date; excludes cancelled appointments
     - Edge case: emergency-superseded patient sees honest new wait

  Refresh: client polls (e.g. 30s) — no WebSockets per TRD.
```

### 2.A.3 — Clinical Enhancements

```
┌──────────────────────────────────────────────────────────────┐
│ MOD-12: Speciality EMR Templates (FR-12-01/02)               │
└──────────────────────────────────────────────────────────────┘
  [FR-12-01] Doctor in consult screen:
     GET /api/v1/consult-templates?specialty=dental
     └─► Returns built-in (DoctorId=null) + their own customs
     Built-in seeded (per survey 25%/17%/17%):
       - dental: 8 sections (chief_complaint, dental_history, etc.)
       - general: 9 sections (hpi, pmh, examination, ...)
       - ayurveda: 9 sections (hetu, prakriti, vikriti, ...)

  Client renders form from structure JSON ──► Doctor edits per patient
  Maps section values to consultation request fields
  Templates pre-fill, NEVER auto-submit (FR-14's amendment principle)

  [FR-12-02] Doctor creates custom:
     POST /api/v1/consult-templates
     { specialty, name, structure: { sections: [...] } }
     └─► Doctor-scoped (other doctors in same clinic don't see it)

  Edge case: template deleted while draft in use ──► draft unaffected
  (templates are starting point, not live-linked)

┌──────────────────────────────────────────────────────────────┐
│ MOD-13: Notification Rules Engine (FR-13-01/02/03)            │
└──────────────────────────────────────────────────────────────┘
  [FR-13-02] Clinic Admin creates message templates:
     POST /api/v1/message-templates
     { name, channel: "whatsapp", content }
     └─► TemplateApprovalStatus=Pending (Meta/BSP approval external)
         Placeholders limited to {{patient_name}}, {{clinic_name}},
         {{date}}, {{time}}, {{precheck_link}}. The dispatch path cannot
         substitute anything else, so an unknown token is rejected on write.
     PATCH /api/v1/message-templates/{id} { name?, content?, approvalStatus? }
       └─► Records the decision Meta/BSP made out of band; nothing is sent
           to Meta from here. Editing the body resets the status to pending
           (Meta re-reviews new content). While a template is not Approved,
           delivery fails approved_template_required — never silently sent.

  [FR-13-01] Clinic Admin configures rules:
     POST /api/v1/notification-rules
     { ruleType: "remindndaysbefore|remindifnovisitnmonths",
       timingConfig: { daysBefore: 0-365 }   // remindndaysbefore
                   | { months: 1-24 }        // remindifnovisitnmonths
       templateId, active: true }
       └─► Exactly one timing key per rule type: the API stores only the key
           the worker reads and rejects any other shape.
     PATCH /api/v1/notification-rules/{id} { active: false }

     appointmentconfirmation / appointmentreminder are system-owned
     (booking flow + ReminderSchedulerService). POST rejects them with
     rule_type_managed_by_system — a rule row for them would never fire.

  [FR-13-03] NotificationRulesWorker (BackgroundService, 5-min):
     1. Drain the NotificationIntent outbox (booking confirmation intents)
     2. Read active rules for current tenant
     3. remindndaysbefore → appointments on today+daysBefore with consent
        remindifnovisitnmonths → patients with no visit for N months
          (dormant unless NotificationRules:NoVisitPolicyApproved=true)
     4. Send through INotificationChannel → Meta Graph API when a whatsapp
        TenantIntegration with a numeric merchantId is enabled; otherwise
        the send is rejected with channel_not_configured
     5. Every outcome becomes a NotificationLog row, including skips:
        consent_missing · approved_template_required · patient_day_guard
        (max one notification per patient per calendar day) ·
        retry_exhausted after 3 attempts
     6. Per-rule exception isolation (one bad rule doesn't crash worker)

  Phase 1 FR-20/21 remain default rules:
    - appointmentconfirmation: wired into booking flow
    - appointmentreminder: 1 day before, fired by ReminderSchedulerService
      (the MOD-13 worker skips this type — booking owns it)

  Edge case: two rules eligible for the same patient on the same day →
  the second send is skipped and logged as patient_day_guard
  (one message per patient per day, not two).

  [FR-13-03] Delivery history:
     GET /api/v1/notification-logs?status=&page=&pageSize=
       └─► Clinic Admin only, tenant-scoped through the appointment,
           append-only (REVOKE UPDATE, DELETE) so there is no write verb
```

### 2.A.4 — Operational Data

```
┌──────────────────────────────────────────────────────────────┐
│ MOD-09: Inventory (FR-09-01/02/03)                           │
└──────────────────────────────────────────────────────────────┘
  [FR-09-01] Clinic Admin/Receptionist:
     POST /api/v1/inventory/items
     { name, tier: "dead|consumable|usable", unit, lowStockThreshold? }
     PATCH /api/v1/inventory/items/{id} { name?, unit?, active?, lowStockThreshold? }
       └─► Soft-deactivate (active=false), NEVER hard delete
           (historical stock_movements must still reference item by ID)

  [FR-09-02] Staff logs in/out:
     POST /api/v1/inventory/items/{id}/movements
     { quantity: int>0, direction: "in|out", note? }
       │
       ▼
     - Inserts StockMovement row (append-only)
     - Returns { balanceBefore, balanceAfter, wouldGoNegative, warning }
     - Edge case: movement would take balance negative → WARN, don't block
       (physical stock corrections happen; a hard block would just
       get staff to enter fake numbers to get past it)

     GET /api/v1/inventory/items/{id}/movements ──► audit history

  [FR-09-03] Clinic Admin:
     GET /api/v1/inventory/low-stock ──► items where balance ≤ threshold

  Tiers flagged for pilot validation per FRD: confirm 3-way split
  matches how clinics actually think about stock.

┌──────────────────────────────────────────────────────────────┐
│ MOD-10: Wishlist (FR-10-01)                                  │
└──────────────────────────────────────────────────────────────┘
  Doctor/ClinicAdmin:
     POST /api/v1/wishlist-items { text, category: "task|goal|equipment|expansion" }
     GET /api/v1/wishlist-items?status=open|done|cancelled
     PATCH /api/v1/wishlist-items/{id} { text?, category?, status? }
       │
       ▼
  Deliberately minimal: NO reminders, NO due dates, NO staff
  assignment (ponytail discipline: this is a note-taking list,
  not a project-management tool).

┌──────────────────────────────────────────────────────────────┐
│ MOD-08: Lab Records & Diagnostics (FR-08-01/02/03)            │
└──────────────────────────────────────────────────────────────┘
  [FR-08-01] Doctor orders:
     POST /api/v1/consultations/{id}/lab-orders
     { testName, notes? }
       └─► Status=Ordered; links to consultation, denormalizes
           patientId+doctorId for fast worklist queries

  [FR-08-01b] Nurse / ClinicAdmin collects specimen:
     POST /api/v1/lab-orders/{id}/collect-sample
     { specimenType, sampleBarcode, collectionNotes? }
       └─► Status=SampleCollected; records sample barcode, specimen type
           (e.g., EDTA Whole Blood, Plain Serum, Fluoride Plasma) & timestamp

  [FR-08-02] Doctor / ClinicAdmin enters result:
     PATCH /api/v1/lab-orders/{id}/result
     (multipart: resultText?, referenceRange?, units?, isAbnormal, file?)
       │
       ▼
     - Creates new LabResult row with Version = latest+1
     - PreviousVersionId chain (clinical amendment, never overwrite)
     - Standard Indian OPD diagnostic templates auto-fill reference ranges
       (CBC, FBS, PPBS, HbA1c, Serum Creatinine, LFT, Lipid Profile, TSH, Urine, Electrolytes)
     - File saved with authenticated binary envelope (HCLAB01 AES-256-GCM)
     - IsAbnormal flag triggers critical panic alert badge (🚨 Abnormal)
     - Status → Resulted

  [FR-08-02b] Doctor Reviews & Signs Off:
     POST /api/v1/lab-orders/{id}/doctor-review
     { notes? }
       └─► Status=ReviewedByDoctor; records doctor timestamp & review notes

  [FR-08-03] Diagnostic Worklist & Branded Reporting:
     GET /api/v1/lab-orders?status=ordered|samplecollected|resulted|reviewedbydoctor
       └─► Sort: pending stages first, then createdAt desc
       └─► Multi-parameter search: patient name, phone, test name, barcode
       └─► Panic Alert Toggle: filter exclusively for abnormal/critical reports
       └─► Branded Diagnostic Slip: generates printable lab report with clinic letterhead
           (organizationName, address, contact), patient demographics, test results,
           reference ranges, and digital sign-off stamp.

  Edge case: consultation amended after order created ──► order
  still links to original consultation ID, unaffected.
  Clinical integrity: results are immutable; corrections create v2+ referencing v1.

┌──────────────────────────────────────────────────────────────┐
│ MOD-11: Finance Ledger (FR-11-01/02/03)                      │
└──────────────────────────────────────────────────────────────┘
  [FR-11-01] Clinic Admin:
     GET /api/v1/ledger/income?month=YYYY-MM
       └─► Sums completed payments in that month
       └─► NO separate income table — always reconciles to payments
           (FR-11-01 acceptance: divergence is a bug, not variance)

  [FR-11-02] Clinic Admin:
     POST /api/v1/ledger/expenses
     { category: "rent|utilities|salaries|supplies|equipment|
                  marketing|professionalservices|insurance|taxes|other",
       categoryOther?: string,
       amount: decimal>0,
       expenseDate: YYYY-MM-DD,
       note? }
       └─► 10 fixed categories per FR-11-02 (NOT full chart-of-accounts)

     PATCH /api/v1/ledger/expenses/{id}
       └─► Edit sets EditedAt timestamp (audit trail per FR-11-02 edge case)

     GET /api/v1/ledger/expenses?month=YYYY-MM

  [FR-11-03] Clinic Admin:
     GET /api/v1/ledger/summary?month=YYYY-MM&format=json
       └─► Returns:
         { month, income, expenses: { total, byCategory: [...] }, net,
           exportFormat: "json" }
       └─► format= CSV/PDF EXPLICITLY DEFERRED (FR-11-03 acceptance:
           "the actual export format pilot clinics' accountants want"
           needs a real answer before this FR is fully specified)

┌──────────────────────────────────────────────────────────────┐
│ MOD-14: Platform Admin Portal (FR-14-01/02/03/04)            │
└──────────────────────────────────────────────────────────────┘
  GATED on Tier 2 tenancy activation (per FRD §11).
  Endpoints built + tested today, functionally idle until second
  tenant signs up. Seeded user: platform-admin@samstack.ai.

  [FR-14-01] SAMSTACK staff (PlatformAdmin role):
     GET /api/v1/platform-admin/tenants?q=search
       └─► Lists all clinics (single-tenant now, multi-tenant in Tier 2)
       └─► Shows: name, subscriptionTier, status, activatedModules, organizationType, specialties
     POST /api/v1/platform-admin/tenants
       └─► Provisions tenant, clinic, admin invite, and initial organizationType + registered clinical specialties

  [FR-14-02] SAMSTACK staff:
     GET /api/v1/platform-admin/tenants/{id}
     PATCH /api/v1/platform-admin/tenants/{id}
     { subscriptionTier?, subscriptionStatus?, activatedModules?,
       subscriptionEndsAt?, organizationType?, specialties? }
       └─► Configures subscription plan, module entitlements, facility classification (Clinic vs Polyclinic),
           and clinical specialties that scope the CRM & queue matrix
       └─► Every change is highest-privilege audit-tracked
           (same standard as financial mutations)

  [FR-14-03] SAMSTACK staff (the single highest-risk feature):
     POST /api/v1/platform-admin/impersonate
     { tenantId, userId, reason? }
       │
       ▼
     - Inserts ImpersonationLog row (startedAt, reason, who impersonated)
     - Returns warning: "impersonation_active_session_logged"
     - In real impl: mints short-lived token for target user with
       impersonatedBy claim. Out of scope for Phase 1 cut.

     POST /api/v1/platform-admin/impersonate/{id}/end ──► sets endedAt
     GET  /api/v1/platform-admin/impersonations?tenantId= ──► full audit

     FR-14-03 acceptance: impersonation doesn't grant extra privilege
     (impersonated session can only do what the impersonated user's
     own role could).

  [FR-14-04] SAMSTACK staff:
     GET  /api/v1/platform-admin/tenants/{id}/flags
     PATCH /api/v1/platform-admin/tenants/{id}/flags/{flagName}
     { enabled: bool }
       └─► Minimal flag infra (TRD_Phase1 §10 explicitly deferred general
           flags; this is the first real justification, minimal version)

  Security: [AuthorizeRoles("PlatformAdmin")] on controller +
  defense-in-depth IsPlatformAdmin() check.
  Clinic admin gets 403 on all endpoints (boundary tested).
```

---

## End-to-End Patient Journey

```
DAY 1: First Visit
─────────────────────────────────────────────────────────────
[Reception]
  1. Receptionist registers patient (FR-06/09)
     POST /patients { name, phone, dob, consent }
  2. Schedules appointment (FR-10)
     POST /appointments { patientId, doctorId, date, timeSlot }
       └─► Auto: pre-check token + WhatsApp confirmation
  3. Patient receives WhatsApp with pre-check link (MOD-23)
     Patient opens link on phone, fills form
     POST /precheck/{token} { chiefComplaint, ... }
  4. Day of visit: patient arrives, checked in (FR-12)
     POST /appointments/{id}/check-in ──► queueToken
  5. Patient opens live tracking link (MOD-25)
     GET /queue-status/{token} ──► see wait

[Consultation]
  6. Doctor opens consult screen
     GET /appointments/{id}/precheck (MOD-23 FR-23-03)
     GET /consult-templates?specialty= (MOD-12 FR-12-01)
  7. Doctor creates consultation (FR-14)
     POST /appointments/{id}/consultation { chiefComplaint, ... }
  8. Doctor orders lab if needed (MOD-08 FR-08-01)
     POST /consultations/{id}/lab-orders { testName }

[Billing]
  9. Receptionist generates invoice (FR-17)
     POST /invoices { appointmentId, lineItems }
  10. Patient pays (FR-18)
      POST /invoices/{id}/payment { method: "cash" } (or Razorpay link)
      Razorpay webhook arrives ──► marks Paid

DAY 2-N: Follow-up
─────────────────────────────────────────────────────────────
  - Patient gets WhatsApp reminder (FR-21 → MOD-13 default rule, 1 day before)
  - Doctor amends consultation if needed (FR-14 amendment, never overwrite)
  - Lab result arrives (MOD-08 FR-08-02)
    PATCH /lab-orders/{id}/result { resultText, file }
  - Doctor writes prescription (FR-15)
    POST /consultations/{id}/prescriptions { items }

OPERATIONS (parallel to clinical)
─────────────────────────────────────────────────────────────
  - Receptionist flags walk-in as emergency (MOD-24 FR-24-01)
    PATCH /appointments/{id}/priority { priority: "emergency" }
  - Clinic Admin reviews low stock (MOD-09 FR-09-03)
    GET /inventory/low-stock
  - Clinic Admin reviews monthly ledger (MOD-11 FR-11-03)
    GET /ledger/summary?month=YYYY-MM
  - Doctor notes equipment wish (MOD-10)
    POST /wishlist-items { text, category: "equipment" }
```

## Frontend Navigation & Role Workspaces

The frontend uses a URL-derived workspace domain filter (`DashboardLayout.tsx`) to eliminate sidebar clutter while strictly preserving all 18 routes and permissions:

| Workspace Domain | Primary Route | Filtered Sidebar Modules | Default Roles on Login |
| :--- | :--- | :--- | :--- |
| **Clinical Care** (`clinical`) | `/dashboard` | Dashboard, Appointments, Patients, Queue, Consultations, Lab Orders | **Doctor** |
| **Pharmacy Services** (`pharmacy`) | `/dashboard/pharmacy/pos` | Pharmacy POS, Drug Batches, Drug Compliance | — |
| **Clinic Operations** (`admin`) | `/dashboard/billing` | Billing, Inventory, Wishlist, Reports, Messages, Staff, Nurse Roster, Platform Admin | **ClinicAdmin** |
| **All Modules** (`all`) | `/dashboard` | All role-permitted modules rendered | **Pharmacist, Nurse, Receptionist** |

- **Multi-Domain Roles** (`Doctor`, `ClinicAdmin`) have an in-sidebar Workspace Domain dropdown selector and a 4-card Launchpad on their dashboard for instant cross-domain access.
- **Single-Domain Roles** (`Pharmacist`, `Nurse`, `Receptionist`) land directly on their full workspace with zero-click overhead.
- **Mobile Navigation** is role-aware: Pharmacists receive `Home`, `POS`, `Batches`, `Compliance`, `Menu`; Nurses receive `Home`, `Patients`, `Queue`, `Roster`, `Menu`; Doctors/Admins/Receptionists receive `Home`, `Patients`, `Appts`, `Queue`, `Menu`.
- **Settings** is pinned in the persistent sidebar footer utility section, ensuring authorized roles can access settings without mutating their active operational domain.

---

## Endpoint Map (Complete)

```
AUTH & USERS
  POST   /api/v1/auth/login
  POST   /api/v1/auth/refresh
  GET    /api/v1/auth/.well-known/jwks.json
  POST   /api/v1/auth/request-password-reset
  POST   /api/v1/auth/confirm-password-reset
  POST   /api/v1/staff/invite
  POST   /api/v1/staff/accept-invite
  GET    /api/v1/staff
  GET    /api/v1/users?role=                         (Active selected-clinic membership; no PlatformAdmin)
  GET    /api/v1/staff/me/credentials                 (Doctor's own selected-clinic signing credentials)
  PATCH  /api/v1/staff/me/credentials                 (Doctor self-service; audited)
  PATCH  /api/v1/staff/{userId}/credentials           (ClinicAdmin/Owner; selected-clinic doctor; audited)

CLINIC
  GET    /api/v1/clinic/profile
  PUT    /api/v1/clinic/profile
  PUT    /api/v1/clinic/hours
  POST   /api/v1/clinic/holidays
  DELETE /api/v1/clinic/holidays/{id}
  POST   /api/v1/clinic/special-hours
  DELETE /api/v1/clinic/special-hours/{id}

PATIENTS
  POST   /api/v1/patients                            (FR-06 / UHID / family phone aware)
  GET    /api/v1/patients                            (patients.read, including Nurse)
  GET    /api/v1/patients/paged                      (patients.read, including Nurse)
  GET    /api/v1/patients/summary                    (patients.read, including Nurse)
  GET    /api/v1/patients/search?q=                  (FR-07 / permissioned & audited)
  GET    /api/v1/patients/{id}                       (FR-07)
  PATCH  /api/v1/patients/{id}                       (FR-08)
  POST   /api/v1/patients/{id}/consents/{cId}/withdraw (FR-09 / DPDP consent withdrawal)

APPOINTMENTS
  POST   /api/v1/appointments                       (FR-10 / double-booking protected)
  GET    /api/v1/appointments?date=&doctorId=&status= (FR-11)
  PUT    /api/v1/appointments/{id}                  (FR-11 / state machine transitions)
  POST   /api/v1/appointments/{id}/reschedule        (FR-13 / slot validation & history)
  POST   /api/v1/appointments/{id}/check-in          (FR-12 / atomic per-doctor counter)
  PATCH  /api/v1/appointments/{id}/priority         (MOD-24 / structured triage & ER referral)

CLINICAL
  POST   /api/v1/appointments/{id}/consultation
  POST   /api/v1/consultations/{id}/amend
  POST   /api/v1/consultations/{id}/prescriptions
  POST   /api/v1/consultations/{id}/lab-orders      (MOD-08)
  PATCH  /api/v1/lab-orders/{id}/result              (MOD-08)
  GET    /api/v1/lab-orders?status=                  (MOD-08)

BILLING, PAYMENTS & LEDGER (Section 5)
  POST   /api/v1/invoices                            (Sec 5.1 / Build draft invoice with per-line tax)
  GET    /api/v1/invoices                            (Sec 5.1 / Filter by patientId, status, date)
  GET    /api/v1/invoices/{id}                       (Sec 5.1 / Invoice details with line items)
  POST   /api/v1/invoices/{id}/finalize              (Sec 5.4 / Immutable finalize & gapless INV-YYYY-XXXXXX)
  GET    /api/v1/invoices/{id}/print                 (Sec 5.4 / Finalized-only JSON receipt payload + duplicate watermark)
  POST   /api/v1/invoices/{id}/payments/dynamic-qr   (Sec 5.5 / NPCI dynamic UPI QR with backup account toggle)
  POST   /api/v1/invoices/{id}/payments/report       (Sec 5.6 / Patient reported-paid hint)
  POST   /api/v1/invoices/{id}/payments/confirm      (Sec 5.6 / Staff bank credit verify + mandatory UTR, no double credit)
  POST   /api/v1/invoices/{id}/credit-notes          (Sec 5.8 / ClinicAdmin credit note, finalized-only, capped)
  GET    /api/v1/payee-accounts                      (Sec 5.7 / List active clinic payee accounts)
  POST   /api/v1/payee-accounts                      (Sec 5.7 / ClinicAdmin only, password + MFA step-up, 24h cooling-off)
  PATCH  /api/v1/payee-accounts/{id}                 (Sec 5.7 / Edit priority, daily limit, active status; step-up required)
  GET    /api/v1/ledger/summary                      (Sec 5.9 / Append-only EOD reconciliation summary)
  GET    /api/v1/ledger/daily-collection             (Sec 5.9 / Daily collection by payment tender)
  GET    /api/v1/ledger/outstanding-dues             (Sec 5.9 / Patient aging accounts receivable)

PRE-CHECK
  POST   /api/v1/precheck/{token}                    (MOD-23)
  GET    /api/v1/appointments/{id}/precheck           (MOD-23)

QUEUE
  GET    /api/v1/queue-status/{token}                 (MOD-25)

CONSULT TEMPLATES
  GET    /api/v1/consult-templates?specialty=         (MOD-12)
  POST   /api/v1/consult-templates                    (MOD-12)

NOTIFICATION RULES
  GET    /api/v1/notification-rules                   (MOD-13)
  POST   /api/v1/notification-rules                   (MOD-13)
  PATCH  /api/v1/notification-rules/{id}              (MOD-13)
  GET    /api/v1/message-templates                    (MOD-13)
  POST   /api/v1/message-templates                    (MOD-13)
  PATCH  /api/v1/message-templates/{id}               (MOD-13) — edit body, record Meta approval
  GET    /api/v1/notification-logs                    (MOD-13) — delivery history, status filter, paged

INVENTORY
  GET    /api/v1/inventory/items                      (MOD-09)
  POST   /api/v1/inventory/items                      (MOD-09)
  PATCH  /api/v1/inventory/items/{id}                 (MOD-09)
  POST   /api/v1/inventory/items/{id}/movements       (MOD-09)
  GET    /api/v1/inventory/items/{id}/movements       (MOD-09)
  GET    /api/v1/inventory/low-stock                  (MOD-09)

WISHLIST
  GET    /api/v1/wishlist-items?status=               (MOD-10)
  POST   /api/v1/wishlist-items                       (MOD-10)
  PATCH  /api/v1/wishlist-items/{id}                  (MOD-10)

LEDGER
  GET    /api/v1/ledger/income?month=                 (MOD-11)
  POST   /api/v1/ledger/expenses                      (MOD-11)
  PATCH  /api/v1/ledger/expenses/{id}                 (MOD-11)
  GET    /api/v1/ledger/expenses?month=               (MOD-11)
  GET    /api/v1/ledger/summary?month=                (MOD-11)

PLATFORM ADMIN
  GET    /api/v1/platform-admin/tenants               (MOD-14)
  GET    /api/v1/platform-admin/tenants/{id}          (MOD-14)
  PATCH  /api/v1/platform-admin/tenants/{id}          (MOD-14)
  POST   /api/v1/platform-admin/impersonate           (MOD-14)
  POST   /api/v1/platform-admin/impersonate/{id}/end  (MOD-14)
  GET    /api/v1/platform-admin/impersonations        (MOD-14)
  GET    /api/v1/platform-admin/tenants/{id}/flags    (MOD-14)
  PATCH  /api/v1/platform-admin/tenants/{id}/flags/{flagName}  (MOD-14)

PHARMACY CATALOG (PharmacyCatalogController)
  GET    /api/v1/pharmacy/drugs                       (MOD-15 Drug Master CRUD)
  GET    /api/v1/pharmacy/drugs/export                (MOD-15 CSV Catalog Export)
  GET    /api/v1/pharmacy/drugs/{id}                  (MOD-15 Drug Details)
  POST   /api/v1/pharmacy/drugs                       (MOD-15 Create Drug)
  GET    /api/v1/pharmacy/substitutes/{drugId}        (MOD-15 Generic Substitutes)

PHARMACY BATCHES & PO (PharmacyBatchController)
  GET    /api/v1/pharmacy/drugs/{id}/batches/fefo     (MOD-15 FEFO Batch List)
  GET    /api/v1/pharmacy/drugs/{id}/fefo-batch       (MOD-15 Next Active FEFO Batch)
  POST   /api/v1/pharmacy/drugs/{id}/batches          (MOD-15 Stock Inwarding)
  POST   /api/v1/pharmacy/purchase-orders/{id}/receive (MOD-15 PO Receive & Batch Inwarding)

PHARMACY POS & SALES (PharmacyPosController)
  POST   /api/v1/pharmacy/checkout                    (MOD-15/16 Idempotent POS Checkout)
  POST   /api/v1/pharmacy/returns/{invoiceId}         (MOD-15/16 POS Returns & Stock Restoral)
  GET    /api/v1/pharmacy/stats                       (MOD-15/16 Sales & Inventory Stats)

PHARMACY COMPLIANCE & DISPENSING (PharmacyComplianceController)
  POST   /api/v1/pharmacy/dispense                    (MOD-16 Rx Validation & Dispense)
  GET    /api/v1/pharmacy/compliance/register         (MOD-16 Schedule H/H1/NDPS/X Registers)
  GET    /api/v1/pharmacy/suppliers                   (MOD-17 Supplier List)
  POST   /api/v1/pharmacy/suppliers                   (MOD-17 Create Supplier)
```

---

## State & Background Workers

| Worker | Interval | Purpose |
|---|---|---|
| `ReminderSchedulerService` (Phase 1) | 1 hour | FR-21 daily batch — sends 24h-before reminders (now also covered by MOD-13 `appointmentreminder` rule) |
| `RazorpayReconciliationWorker` | 60 min | FR-18 safety net — polls pending Razorpay payments, marks Paid if webhook missed |
| `NotificationRulesWorker` (Phase 2) | 5 min | MOD-13 — evaluates all active rules, fires via INotificationService with idempotency check |

---

## Security Boundaries (Verified)

- **Clinic scope**: `tenant_id` comes from the signed JWT claim only; EF Core global query filter + Postgres RLS both enforce it
- **Role enforcement**: `[AuthorizeRoles]` + `RbacHandler` reads the `"role"` claim; money/clinical writes also require a named permission
- **Mandatory MFA**: ClinicAdmin, Doctor, Receptionist and PlatformAdmin cannot use the console before enrolling a second factor
- **Audit append-only**: DB role `app_user` has `REVOKE UPDATE, DELETE` on the audit tables (patient, notification, auth, break-glass, controlled substances…) via `scripts/setup-least-privilege-db.sql`
- **Auth audit**: every login success/failure/lockout, MFA change, refresh reuse, tenant switch, role grant and revoke-all is written to `AuthAuditLogs`
- **Refresh tokens**: SHA-256 digest in DB, rotated per use, family-revoked on reuse, `httpOnly` cookie only (never in JS storage)
- **Inactivity**: 30-min sliding window for Doctor/Admin, in-memory cache
- **Webhook HMAC**: Constant-time compare for `X-Razorpay-Signature`
- **Platform Admin boundary**: `[AuthorizeRoles("PlatformAdmin")]` + defense-in-depth `IsPlatformAdmin()` check. Clinic admin → 403 (verified)

---

## Out of Scope (Explicit)

- **Pharmacy Track 2 Remainder** (MOD-15/16/17 core is shipped via 4 dedicated controllers; remaining Track 2 extended items out of scope)
- **AI features** (MOD-18/19) — Track 3
- **IPD** (MOD-20–26) — Track 4
- **UAE adapter** — International
- **Voice Agent** (MOD-27) — Phase 3 per FRD-Phase2 §9
- **Offline sync** (FR-22) — deferred (see state/technical-debt.md item 7)
- **Real WhatsApp / Razorpay vendor accounts** — the Meta Graph send path and the Razorpay
  payment/webhook path are implemented; both stay inactive until a tenant integration row
  (`whatsapp` with a numeric merchantId, or `razorpay` keys) is configured. Awaiting pilot
  vendor accounts (OpenIddict in-house OIDC active).
- **S3 blob storage** for MOD-08 — local disk `lab-uploads/` for now

---

## References

- `FRD-Phase-2-FINAL.md` — Phase 2 master spec (all 9 modules)
- `TRD-Phase2-V1.md` — Phase 2 technical reference (Hangfire, blob storage)
- `PRD-Phase2-V1.md` — Phase 2 product reference (patient journey stories)
- `samstack-ai-frd-phase1-FINAL.md` — Phase 1 master spec (FR-01–22)
- `memory.md` — Session working memory
- `agents/state/*.md` — Project state files
