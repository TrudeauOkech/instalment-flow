# ADR-001: Product scope

- Status: Accepted (2026-11-18)

## Context
Lenders and asset financiers in Kenya accept irregular mobile-money payments. Most already run a loan management system (LMS). A library that tried to replace the LMS would not be adopted. A library limited to transport (M-Pesa SDKs) does not solve correctness.

## Decision
InstalmentFlow is **an open-source, multi-tenant .NET library for managing flexible loan repayments, partial payments, instalment schedules and payment allocation.**

- **It is the source of truth for:**
  - payments received and their identification;
  - unapplied funds;
  - allocations;
  - outstanding amounts per obligation component;
  - instalment status;
  - the movement journal.
- **It is *not* the source of truth for the loan account as a whole.** Customer KYC, credit decisions, loan terms, disbursement and pricing stay in the host or LMS. An obligation carries the host's external ID, and the two systems stay in sync through outbox events and queries.
- **It is not:** a complete LMS, a payment gateway, an accounting platform (general ledger), or an interest calculation engine.

## Consequences
- The README has a "What InstalmentFlow is not" section.
- Feature requests outside scope are declined, or handled as optional packages with their own ADR.
- Journal export (`ReadJournalAsync`) is the bridge to accounting. We never own the chart of accounts of the host's general ledger.
