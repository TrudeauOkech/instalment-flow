# Current due first (`current-due-first@1`)

Pays each obligation's *current* instalment first, meaning its most recently due one, and then the arrears,
oldest first. This keeps the current period from going overdue while older arrears are worked down.

## Current instalment before arrears

```allocation-spec
strategy current-due-first@1
as-of 2026-12-03
pay 70
obligation A opened 1
  2026-12-01 principal 50
  2026-12-02 principal 50
  2026-12-03 principal 50
expect A#3 principal 50
expect A#1 principal 20
expect unapplied 0
```

## Every obligation's current instalment, in due order, then arrears

Loan B's current instalment (2 December) falls due before loan A's (3 December).

```allocation-spec
strategy current-due-first@1
as-of 2026-12-03
pay 100
obligation A opened 1
  2026-12-01 principal 40
  2026-12-03 principal 40
obligation B opened 2
  2026-12-02 principal 40
expect B#1 principal 40
expect A#2 principal 40
expect A#1 principal 20
expect unapplied 0
```
