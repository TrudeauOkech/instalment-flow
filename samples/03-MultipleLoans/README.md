# 03 — Multiple loans per customer

A customer with a motorbike loan and a school-fees loan (ADR-009):

1. A payment quoting `BIKE-1` pays the bike only; the surplus stays held for the bike and never spills onto school fees (I-21).
2. A payment quoting nothing is held with an `AllocationInstructionRequired` exception; an operator allocates it to `SCHOOL-1`.
3. A second tenant, whose contracts allow it, opts in to `MultipleObligationsPolicy.AllocateByStrategy` with the
   `proportional@1` strategy, so an unreferenced payment is split in proportion to what is due on each loan.

```sh
dotnet run --project samples/03-MultipleLoans
```
