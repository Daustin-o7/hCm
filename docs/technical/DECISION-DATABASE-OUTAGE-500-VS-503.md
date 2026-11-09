# Architectural Decision Record: Whole-Database Outage Contract (Gate G02)

**Decision ID:** ADR-G02-OUTAGE  
**Date:** 2026-11-09  
**Status:** **ACCEPTED & SETTLED**  
**Resolution:** Return **RFC 7807 HTTP 503 (`Service Unavailable`)** for transient database outages, preserving **HTTP 500 (`Internal Server Error`)** for unhandled application faults.

---

## 1. Context & Problem Statement

In Gate G02 ("Tenant isolation & whole-database outage behavior"), the system required an authoritative architectural decision:
When a PostgreSQL database outage occurs (e.g. database server restart, connection pool exhaustion, transient network partition, or socket disconnect), should the API return:
- **Option A:** Generic `HTTP 500 Internal Server Error` (default ASP.NET Core `UseExceptionHandler()` behavior)
- **Option B:** Sanitized `HTTP 503 Service Unavailable` with RFC 7807 `application/problem+json` payload (matching existing search outage fallback and payment gateway retry semantics)

### Critical Requirements:
1. **Zero Information Leakage:** Under no circumstances may internal PostgreSQL error messages, SQL state codes, table/column names, connection strings, or server hostnames be exposed to API consumers or client browsers.
2. **Clear Client Retry Semantics:** PWA client outbox, offline synchronization, and background workers must understand that a database connection drop is transient and safe to retry with exponential backoff (HTTP 503 + `Retry-After`), rather than a fatal programming defect (HTTP 500).
3. **Consistency Across Modules:** Existing search fallback (`MedicinesController`, `PatientsController`) and payment gateway connectivity already return HTTP 503 for transient downstream outages.

---

## 2. Decision

The platform adopts **Option B (HTTP 503 Service Unavailable for Transient Database Outages)**:

1. **Transient Database Outages → HTTP 503:**
   - Detect `NpgsqlException` where `IsTransient == true`, or where the underlying exception is a `SocketException` or `TimeoutException`, or where the PostgreSQL state is an active connection/shutdown code (`08000`, `08003`, `08006`, `08001`, `08004`, `57P01`, `57P02`, `57P03`).
   - Emit an RFC 7807 compliant problem details response:
     ```json
     {
       "type": "https://tools.ietf.org/html/rfc7807",
       "title": "Service Unavailable",
       "status": 503,
       "detail": "The database service is temporarily unavailable. Please retry shortly.",
       "instance": "/api/v1/..."
     }
     ```
   - Standard response headers:
     `Content-Type: application/problem+json`  
     `Retry-After: 5`

2. **Application Faults & Disposed Contexts → HTTP 500:**
   - Non-transient exceptions (e.g. `ObjectDisposedException`, `NullReferenceException`, unexpected invalid state) continue to return `HTTP 500 Internal Server Error` with a sanitized problem details body (as verified in `Part2SearchReportReportingTests.cs:54`).

---

## 3. Implementation Design

An ASP.NET Core `IExceptionHandler` (`DatabaseOutageExceptionHandler`) is added to the DI container in `Program.cs`:
- Registered via `builder.Services.AddExceptionHandler<DatabaseOutageExceptionHandler>()`.
- Positioned before default fallback handler so transient DB faults are intercepted cleanly without stack trace emission.
- Logs full diagnostic details to internal Serilog (`Log.Warning(ex, "Transient database connectivity outage on {Path}", context.Request.Path)`), while emitting only sanitized payload to the client.

---

## 4. Consequences & Verification

- **Positive:** Front desk PWA and mobile apps receive clear retry signals without triggering fatal crash screens.
- **Positive:** Security audit passes: zero PHI or infrastructure leakage.
- **Verification:** Unit and integration tests verify both branches:
  1. Simulated transient network disconnect returns 503 with RFC 7807 payload.
  2. Disposed DbContext / unhandled application bug continues returning sanitized 500.

