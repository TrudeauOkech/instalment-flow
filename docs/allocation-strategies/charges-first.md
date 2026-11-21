# Charges first (`charges-first@1`)

Pays all due penalties, then fees, then interest (each oldest first), and only then principal.

> Use this strategy only where the contract and regulation allow it. It is never a default (risk R-F11).

```allocation-spec
strategy charges-first@1
as-of 2026-12-02
pay 40
obligation A opened 1
  2026-12-01 principal 50 interest 10 penalty 5
  2026-12-02 principal 50 interest 10
expect A#1 penalty 5
expect A#1 interest 10
expect A#2 interest 10
expect A#1 principal 15
expect unapplied 0
```
