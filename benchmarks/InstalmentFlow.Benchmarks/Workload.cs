namespace InstalmentFlow.Benchmarks;

/// <summary>The NFR-4 workload: a customer with 5 obligations of 365 daily instalments, paid by reference.</summary>
public static class Workload
{
    public const string Tenant = "bench-ke";
    public const int Obligations = 5;
    public const int Instalments = 365;

    public static readonly DateOnly Start = new(2026, 1, 1);

    public static void Configure(InstalmentFlowBuilder builder) => builder.AddTenant(Tenant, Currency.KES, "Africa/Nairobi");

    /// <summary>Opens customer <paramref name="customer"/> with its obligations <c>{customer}-L0</c> to <c>{customer}-L4</c>.</summary>
    public static async Task SeedCustomerAsync(IInstalmentFlow flow, string customer)
    {
        var account = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = $"acct-{customer}", ExternalId = customer });
        for (var o = 0; o < Obligations; o++)
        {
            await flow.Obligations.OpenAsync(new OpenObligationRequest
            {
                IdempotencyKey = $"loan-{customer}-{o}", Account = account.AccountId, ExternalId = $"{customer}-L{o}", PaymentReference = $"{customer}L{o}",
                Components = [Component.Principal(Money.Kes(100m * Instalments))], Plan = PlanSpec.Equal(Frequency.Daily, Instalments, Start),
            });
        }
    }

    /// <summary>A KES 70 payment by reference: it settles part of one daily instalment and part of the next.</summary>
    public static RecordPaymentRequest Payment(string customer, int n) => new()
    {
        IdempotencyKey = $"pay-{customer}-{n}",
        Source = new ExternalReference("mpesa:600123", $"{customer}T{n}"),
        Amount = Money.Kes(70m),
        ValueAt = new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero).AddMinutes(n),
        Target = PaymentTarget.ForReference($"{customer}L{n % Obligations}"),
    };
}
