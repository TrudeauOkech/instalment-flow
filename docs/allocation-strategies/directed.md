# Directed payments: no spill-over

A payment that quotes a loan reference, or an STK push for a known loan, may be applied **only to that loan**
(ADR-009, invariant I-21). The tenant's strategy orders that loan's instalments. Anything left stays as the
customer's unapplied funds, even if another of their loans is overdue.

```allocation-spec
strategy oldest-due-first@1
as-of 2026-12-05
pay 300
eligible A
obligation A opened 2
  2026-12-05 principal 100
obligation B opened 1
  2026-12-01 principal 100
expect A#1 principal 100
expect unapplied 200
```
