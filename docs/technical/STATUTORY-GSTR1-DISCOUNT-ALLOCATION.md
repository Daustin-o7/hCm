# Statutory Analysis: GSTR-1 Invoice-Level Discount Allocation & Rule C Reversal

**Document ID:** STAT-GSTR1-DISCOUNT-2026-11-09  
**Authoritative Standards:** Section 15(3) & Section 34 of Central Goods and Services Tax (CGST) Act, 2017; GST Rules (Rule 42/43/53)  
**Governing Gate:** G06 (Accounting Sign-Off & Statutory Returns)  

---

## 1. Statutory Background

Under Section 15(3) of the CGST Act 2017, the value of the supply shall not include any discount which is given:
1. **Section 15(3)(a):** Before or at the time of the supply if such discount has been duly recorded in the invoice issued in respect of such supply; and
2. **Section 15(3)(b):** After the supply has been effected, if such discount is established in terms of an agreement entered into at or before the time of such supply and specifically linked to relevant invoices, and input tax credit as is attributable to the discount on the basis of document issued by the supplier has been reversed by the recipient.

---

## 2. Architecture & Data Model in Hospital CRM

In Hospital CRM:
- Each invoice line item ([`InvoiceLineItem.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Domain/Entities/InvoiceLineItem.cs)) records:
  - `UnitPrice`, `Quantity`, `Amount`
  - `GstRate` (0%, 5%, 12%, 18%)
  - `TaxCategory` (Nil, Exempt, HealthcareServices, TaxableStandard)
  - `HsnSacCode` (e.g. `999312` for healthcare consultations, `3004` for retail pharmaceuticals)
  - `TaxAmount` / `ResolvedTaxAmount()`
  - Optional line-level `DiscountAmount`
- An invoice also has an invoice-level `DiscountAmount` ([`Invoice.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Domain/Entities/Invoice.cs)), typically granted by front desk or clinic owner as a lump-sum concession on the overall bill.

---

## 3. Statutory Treatment in GSTR-1

In the automated GSTR-1 export endpoint ([`ReportsController.cs`](file:///e:/Company/Hospital%20Management/Hospital_CRM/backend/Hospital_CRM.Api/Controllers/ReportsController.cs)):
1. **Table 7 (B2CS - B2C Small Supplies):** Aggregates taxable values and output tax grouped strictly by `GstRate` and `PlaceOfSupply`.
2. **Table 12 (HSN/SAC Summary):** Aggregates quantity and taxable values grouped by `{HsnSacCode, GstRate}`.
3. **Table 9B (CDNUR - Unregistered Credit Notes):** Reverses output GST only when a formal Section 34 Credit Note is issued and approved.

### Invoice-Level Discount Allocation Caveat:
When an invoice carries a lump-sum `DiscountAmount > 0` at the invoice level without being attributed to individual line items:
- The system **does not** arbitrarily guess or apportion the discount across heterogeneous GST slabs (e.g., mixing 0% clinical consults with 12% medicines).
- The system reports line-level taxable values as recorded and flags the statutory warning:
  ```json
  "warnings": [
    "invoice_level_discount_not_apportioned_to_lines"
  ]
  ```
- **Statutory Audit Recommendation for Clinic Accountant:**
  - **Preferred Practice:** Require billing staff to input discounts at the item level (e.g. 10% off pharmacy, or ₹100 off consultation fee), preserving exact HSN-level net taxable values.
  - **Permitted Accounting Alternative:** Where a lump-sum invoice discount is given, the clinic accountant may either:
    1. Pro-rate the discount across taxable lines during monthly GSTR-1 return filing via the GST Portal offline tool; or
    2. Treat the lump-sum invoice discount as a commercial cash discount (financial ledger expense) without reducing statutory GST output liability.

---

## 4. Reconciled Accounting Basis: Management vs Statutory Returns

A clear distinction is maintained between **Internal Management Accounting (Rule C)** and **Statutory Tax Reporting (GSTR-1)**:

### A. Internal Management Accounting (Rule C — Cumulative Collection Basis)
1. **Cumulative Collection Basis:** Clinic management reporting operates on a cumulative collection basis. GST on fees and pharmacy goods is recognized progressively as payments are actually collected.
2. **Refunds Alone Do Not Reverse GST:** A payment refund or payout voucher is an outflow of cash, not a statutory tax document. A refund alone does NOT reduce cumulative recognized GST output liability.
3. **Approved Credit Notes (Section 34):** An approved Credit Note adjusts cumulative recognized GST in the **period of the Credit Note's issue** (not by retroactive alteration of past invoices).
4. **Refund Tax Resolution Gate (Fix 2):** Any refund recorded in the system requires either:
   - A link to an approved Section 34 Credit Note (`refundCreditNoteId`), which enables statutory tax reversal; or
   - An explicit explanation (`refundNonConsiderationReason`) documenting that the payout is a commercial goodwill gesture or non-consideration refund that does not adjust statutory GST.
5. **Net Collections Formula:**
   $$\text{Net Collections} = \sum \text{Confirmed Collections} - \sum \text{Confirmed Refunds}$$

### B. Statutory Tax Returns (GSTR-1 — Invoice-Date Basis)
1. **Statutory Accrual Basis:** In accordance with GST statutory law, GSTR-1 returns report output tax on an **invoice-date / supply-date basis** for the relevant tax period (Table 7 B2CS, Table 12 HSN).
2. **Table 9B (CDNUR):** Credit Notes issued under Section 34 are declared in Table 9B in the tax month of the Credit Note issue.
3. **Statutory Net Tax Payable:**
   $$\text{Statutory Net Tax} = \sum \text{Invoice GST (Table 7)} - \sum \text{Approved Credit Note GST (Table 9B)}$$

---

## 5. Formal Accountant Sign-Off Acceptance

This document forms part of the G06 statutory readiness evidence package. The clinic's chartered accountant / tax auditor must review and countersign this treatment as part of the First-Paid-Activation Go-Live sign-off.
