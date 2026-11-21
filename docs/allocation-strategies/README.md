# Allocation strategies

How InstalmentFlow decides which instalments a payment pays (ADR-006, doc 02 §9). **Every example on these pages
is executable.** The test `StrategySpecTests` runs each `allocation-spec` block against the real allocation engine,
so the documentation cannot drift from the code.

| Strategy | Page |
|---|---|
| `oldest-due-first@1` (default) | [oldest-due-first.md](oldest-due-first.md) |
| `current-due-first@1` | [current-due-first.md](current-due-first.md) |
| `principal-first@1` | [principal-first.md](principal-first.md) |
| `charges-first@1` | [charges-first.md](charges-first.md) |
| `proportional@1` | [proportional.md](proportional.md) |
| Future-instalment policies (`hold`, `next`, `prepay`) | [future-instalments.md](future-instalments.md) |
| Directed payments (no spill-over) | [directed.md](directed.md) |

## How allocation works

1. **Identification** (ADR-009) decides which obligations the funds may be applied to. For a payment quoting a
   loan reference, that is only that loan. Ambiguous payments are never allocated.
2. The **strategy** spreads the funds over the *due* instalments of those obligations. These are instalments due on
   or before the allocation date that still have something outstanding.
3. Whatever is left follows the tenant's **future-instalment policy**:
   - `hold` (the default) keeps it as unapplied funds;
   - `next` pays upcoming instalments in due order;
   - `prepay` pays principal from the last instalment backwards.
4. The engine **validates** every plan: each target must be permitted, open and paid no more than it owes; the
   total must not exceed the funds; and the currency must match. Then it applies the plan with balanced journal
   entries.

Ties are always broken the same way: due date, then the obligation that was opened earlier, then obligation ID, then
instalment number. Within an instalment, components follow the tenant's component order. The default is
principal → interest → fee → penalty, which never puts charges first (risk R-F11).

## Spec format

````
```allocation-spec
strategy oldest-due-first@1           # registered strategy
components principal,interest,fee,penalty   # optional; tenant component order (this is the default)
future hold                           # optional; hold | next | prepay (default hold)
as-of 2026-12-05                      # allocation business date
pay 700                               # KES
eligible A                            # optional; restricts the obligations (default: all)
obligation A opened 1                 # label and opening order
  2026-12-01 principal 300 interest 50   # one instalment per indented line
expect A#1 principal 300              # every allocation must be listed, and nothing else
expect unapplied 150                  # funds left
```
````

Components with a charge code are written `fee.insurance`.
