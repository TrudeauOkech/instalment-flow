# Proportional (`proportional@1`)

Splits the funds across obligations in proportion to each one's due outstanding amount, using the
largest-remainder method so the shares add up exactly. Within each obligation, the share is paid oldest first. A
share never exceeds what that obligation has due.

## Three equal loans: KES 100 cannot split evenly

The extra cent goes to the earliest-opened loan (failure scenario 13).

```allocation-spec
strategy proportional@1
as-of 2026-12-01
pay 100
obligation A opened 1
  2026-12-01 principal 100
obligation B opened 2
  2026-12-01 principal 100
obligation C opened 3
  2026-12-01 principal 100
expect A#1 principal 33.34
expect B#1 principal 33.33
expect C#1 principal 33.33
expect unapplied 0
```

## Unequal due amounts

Loan A has KES 300 due and loan B has KES 100, so KES 200 splits 150 / 50.

```allocation-spec
strategy proportional@1
as-of 2026-12-02
pay 200
obligation A opened 1
  2026-12-01 principal 150
  2026-12-02 principal 150
obligation B opened 2
  2026-12-01 principal 100
expect A#1 principal 150
expect B#1 principal 50
expect unapplied 0
```

## More than is due: everything due is paid and the rest is held

```allocation-spec
strategy proportional@1
as-of 2026-12-01
pay 500
obligation A opened 1
  2026-12-01 principal 300
obligation B opened 2
  2026-12-01 principal 100
expect A#1 principal 300
expect B#1 principal 100
expect unapplied 100
```
