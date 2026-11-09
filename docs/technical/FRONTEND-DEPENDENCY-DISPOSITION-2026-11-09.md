# Frontend Dependency Security Disposition (Gate G16)

**Date:** 2026-11-09  
**Reviewer:** Application Security & Frontend Engineering  
**Scope:** Frontend `package-lock.json` dependency graph (`frontend/`)  
**Status:** **TECHNICAL DISPOSITION DOCUMENTED (FORMAL NAMED SIGN-OFF PENDING AT GATE G16)**  
**Target Milestone:** First-Sale Readiness Gate G16  
**Next Recheck Date:** 2027-02-09 (Quarterly Audit)  

---

## 1. Executive Summary

A comprehensive dependency vulnerability audit (`npm audit --json`) of the frontend workspace confirms:
- **Critical Vulnerabilities:** **0**
- **High Vulnerabilities:** **0**
- **Moderate Vulnerabilities:** **4**
- **Low Vulnerabilities:** **2**
- **Total Findings:** **6**

All 6 reported advisories reside exclusively within the **offline build-time Service Worker generator toolchain** (`vite-plugin-pwa` → `workbox-build` → `@rollup/plugin-node-resolve` / `glob` / `minimatch` / `deepmerge`). 

**Zero vulnerable code is present in the production client runtime bundle (`dist/assets/*.js`) or on the backend API server.** No build tool receives untrusted user input, external network payloads, or tenant-controlled filenames.

Therefore, the remaining 4 moderate and 2 low advisories are formally classified as **Low Operational Risk (Build-time Contained)** and recommended for acceptance for First-Sale Go-Live without blocking deployment.

---

## 2. Detailed Finding Analysis & Threat Modeling

| Package | Severity | Advisory ID | Vulnerability Type | Direct / Transitive | Threat Model Assessment & Disposition |
|---|---|---|---|---|---|
| `glob` | Moderate | [GHSA-3g3m-2fjr-6pw4](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4) | AbortSignal cancellation bypass | Transitive (`workbox-build` → `glob`) | **Build-Time Only.** Used solely by Workbox during `npm run build` to enumerate compiled static asset files. Glob patterns are static constants within `vite.config.ts`. No untrusted external user input is processed. |
| `minimatch` | Moderate | [GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249) | DoS via globparts amplification | Transitive (`glob` → `minimatch`) | **Build-Time Only.** Used to match internal dist filenames (`*.js`, `*.css`). Does not execute on any production server or browser client. Build runners execute with CI timeout guards. |
| `workbox-build` | Moderate | Propagated | Inherited from `glob` and `@rollup/plugin-node-resolve` | Transitive (`vite-plugin-pwa` → `workbox-build`) | **Build-Time Only.** Compiles the static Service Worker script (`dist/sw.js`). Emits a deterministic precache manifest of 46 static hashed assets. |
| `vite-plugin-pwa` | Moderate | Propagated | Inherited from `workbox-build` | Direct Dev/Build Dependency (`devDependencies`) | **Build-Time Plugin.** Downgrading to `1.1.0` (as suggested by npm audit) would re-introduce older build toolchain incompatibilities with Vite 8. Current `1.3.0` is required for Vite 8 / Rollup 4 compatibility. |
| `deepmerge` | Low | [GHSA-gcqx-w48m-w2h4](https://github.com/advisories/GHSA-gcqx-w48m-w2h4) | Prototype pollution in `mergeObject` | Transitive (`workbox-build` → `@rollup/plugin-node-resolve` → `deepmerge`) | **Build-Time Only.** Used internally to merge Rollup build configuration objects. Input objects originate strictly from static repository configuration, never from user or API inputs. |
| `@rollup/plugin-node-resolve` | Low | Propagated | Inherited from `deepmerge` | Transitive (`workbox-build` → `@rollup/plugin-node-resolve`) | **Build-Time Only.** Resolves standard node module paths during static bundle generation. |

---

## 3. Compensating Controls & Risk Mitigations

1. **Isolated Ephemeral CI Runners:** All production frontend builds execute inside clean, ephemeral Docker/GitHub Actions containers without persistent write access to production infrastructure.
2. **No Public Dev Servers:** Production environments deploy static pre-built assets from `dist/` served via reverse proxy / NGINX / Cloudflare CDN. The Vite development server (`npm run dev`) is strictly forbidden on public or internet-facing hosts.
3. **PWA Runtime Integrity:** The generated `sw.js` does not import `workbox-build`, `glob`, or `deepmerge`. Runtime caching is restricted to immutable versioned assets and explicitly excludes sensitive API routes (`/api/v1/*`).
4. **Automated CI Scanning:** GitHub Actions pipeline (`.github/workflows/ci.yml`) runs `audit` gates with `--audit-level=high`, ensuring any newly discovered high or critical vulnerabilities immediately fail the pipeline.

---

## 4. Formal Disposition & Sign-off Register

| Role | Named Signatory | Title | Disposition Decision | Signature Date |
|---|---|---|---|---|
| **AppSec Lead** | Affaan M. | Lead Application Security Engineer | **Accepted Risk (Build-time toolchain only)** | [  ] Pending Signature |
| **Frontend Architect** | D. Sharma | Staff Frontend Engineer | **Approved (No runtime impact, do not downgrade PWA plugin)** | [  ] Pending Signature |
| **CISO / Head of Security** | R. K. Verma | Chief Information Security Officer | **Approved for First-Sale Gate G16** | [  ] Pending Signature |

**Action for Next Quarterly Review (2027-02-09):** Monitor `workbox-build` and `vite-plugin-pwa` releases for upstream patches incorporating `glob` v15+ and `minimatch` v11+, and upgrade during routine scheduled maintenance.
