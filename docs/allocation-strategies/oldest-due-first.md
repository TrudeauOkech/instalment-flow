# Oldest due first (`oldest-due-first@1`, default)

Pays the oldest unpaid due instalment first, across every obligation the funds may be applied to. Within an
instalment, components follow the tenant's component order.

## Arrears are cleared before the current instalment

A daily KES 50 plan, three days due, with KES 120 received.

```allocation-spec
strategy oldest-due-first@1
as-of 2026-12-03
pay 120
obligation A opened 1
  2026-12-01 principal 50
  2026-12-02 principal 50
  2026-12-03 principal 50
  2026-12-04 principal 50
expect A#1 principal 50
expect A#2 principal 50
expect A#3 principal 20
expect unapplied 0
```

## Across two loans, with interest before principal

The tenant pays interest before principal. Loan A's instalment is the oldest; loan B's is next. Loan A's
December 15 instalment is not due yet, so the remaining KES 150 is held.

```allocation-spec
strategy oldest-due-first@1
components interest,principal,fee,penalty
as-of 2026-12-05
pay 700
obligation A opened 1
  2026-12-01 principal 300 interest 50
  2026-12-15 principal 300
obligation B opened 2
  2026-12-03 principal 200
expect A#1 interest 50
expect A#1 principal 300
expect B#1 principal 200
expect unapplied 150
```

## Same due date: the earlier-opened obligation goes first

```allocation-spec
strategy oldest-due-first@1
as-of 2026-12-01
pay 80
obligation A opened 2
  2026-12-01 principal 50
obligation B opened 1
  2026-12-01 principal 50
expect B#1 principal 50
expect A#1 principal 30
expect unapplied 0
```

## A part-paid instalment follows the component order

Only part of the instalment can be paid, so the component order decides what is settled. With the tenant paying
interest before principal, the interest is cleared and the principal is part-paid.

```allocation-spec
strategy oldest-due-first@1
components interest,principal,fee,penalty
as-of 2026-12-01
pay 80
obligation A opened 1
  2026-12-01 principal 100 interest 50
expect A#1 interest 50
expect A#1 principal 30
expect unapplied 0
```

## Same due date within one loan: the earlier instalment goes first

A deposit may fall due on the same day as the first regular instalment. The lower instalment number is paid first.

```allocation-spec
strategy oldest-due-first@1
as-of 2026-12-01
pay 70
obligation A opened 1
  2026-12-01 principal 50
  2026-12-01 principal 50
expect A#1 principal 50
expect A#2 principal 20
expect unapplied 0
```
