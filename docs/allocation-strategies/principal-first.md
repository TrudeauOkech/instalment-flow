# Principal first (`principal-first@1`)

Pays all due principal (oldest first), then all due interest, then fees, then penalties. This reduces the
outstanding balance fastest.

```allocation-spec
strategy principal-first@1
as-of 2026-12-02
pay 125
obligation A opened 1
  2026-12-01 principal 50 interest 10 fee 5
  2026-12-02 principal 50 interest 10 fee 5
expect A#1 principal 50
expect A#2 principal 50
expect A#1 interest 10
expect A#2 interest 10
expect A#1 fee 5
expect unapplied 0
```
