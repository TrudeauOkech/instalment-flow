namespace InstalmentFlow.Domain;

/// <summary>
/// Derives instalment status from balances and a business date. Status is never stored (doc 02 §6.3), so it
/// cannot go stale: the same balances on the same date always give the same answer.
/// </summary>
internal static class InstalmentEvaluator
{
    public static InstalmentPaymentState PaymentState(Instalment instalment)
    {
        if (instalment.Outstanding.IsPositive)
        {
            return instalment.Allocated.IsZero && instalment.Waived.IsZero
                ? InstalmentPaymentState.Unpaid
                : InstalmentPaymentState.PartiallyPaid;
        }

        return instalment.Allocated.IsPositive ? InstalmentPaymentState.Paid : InstalmentPaymentState.Waived;
    }

    public static InstalmentTiming Timing(Instalment instalment, DateOnly asOf, GracePeriod grace)
    {
        if (!instalment.Outstanding.IsPositive)
        {
            return InstalmentTiming.Settled;
        }

        if (asOf < instalment.DueDate)
        {
            return InstalmentTiming.Upcoming;
        }

        if (asOf == instalment.DueDate)
        {
            return InstalmentTiming.Due;
        }

        return asOf <= grace.LastGraceDay(instalment.DueDate) ? InstalmentTiming.InGrace : InstalmentTiming.Overdue;
    }

    /// <summary>Calendar days since the due date while an amount is outstanding; zero otherwise.</summary>
    public static int DaysPastDue(Instalment instalment, DateOnly asOf) =>
        instalment.Outstanding.IsPositive && asOf > instalment.DueDate ? asOf.DayNumber - instalment.DueDate.DayNumber : 0;

    /// <summary>
    /// The due date of the last instalment in the unbroken run of fully covered instalments from the start
    /// (doc 02 §10), or <see langword="null"/> if the first instalment is not covered.
    /// </summary>
    public static DateOnly? PaidThroughDate(IEnumerable<Instalment> instalments)
    {
        DateOnly? paidThrough = null;
        foreach (var instalment in instalments.OrderBy(i => i.Sequence))
        {
            if (instalment.Outstanding.IsPositive)
            {
                break;
            }

            paidThrough = instalment.DueDate;
        }

        return paidThrough;
    }
}
