# Future-instalment policies

After the strategy has paid what is due, the tenant's `FutureInstalments` policy decides what happens to the
rest. Allocations made this way are recorded under `future.next-instalments@1` or `future.prepay-principal@1`,
so it is always clear which rule moved the money.

## `hold` (default): keep it as unapplied funds

```allocation-spec
strategy oldest-due-first@1
future hold
as-of 2026-12-01
pay 120
obligation A opened 1
  2026-12-01 principal 50
  2026-12-02 principal 50
  2026-12-03 principal 50
expect A#1 principal 50
expect unapplied 70
```

## `next`: pay upcoming instalments in due order (pay-as-you-go)

The customer buys days ahead; the paid-through date moves forward.

```allocation-spec
strategy oldest-due-first@1
future next
as-of 2026-12-01
pay 120
obligation A opened 1
  2026-12-01 principal 50
  2026-12-02 principal 50
  2026-12-03 principal 50
expect A#1 principal 50
expect A#2 principal 50
expect A#3 principal 20
expect unapplied 0
```

## `prepay`: pay principal from the last instalment backwards

This shortens the term. Interest on future instalments stays owed; any rebate is a host decision, made with a
waiver.

```allocation-spec
strategy oldest-due-first@1
future prepay
as-of 2026-12-01
pay 120
obligation A opened 1
  2026-12-01 principal 50 interest 5
  2026-12-02 principal 50 interest 5
  2026-12-03 principal 50 interest 5
expect A#1 principal 50
expect A#1 interest 5
expect A#3 principal 50
expect A#2 principal 15
expect unapplied 0
```
