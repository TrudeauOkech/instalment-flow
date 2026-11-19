namespace InstalmentFlow;

/// <summary>Refers to a customer account in the current tenant, by ID or by the host's external ID.</summary>
public sealed record AccountRef
{
    private AccountRef(AccountId? id, string? externalId)
    {
        Id = id;
        ExternalId = externalId;
    }

    /// <summary>The account ID, when referring by ID.</summary>
    public AccountId? Id { get; }

    /// <summary>The host's ID for the customer, when referring by external ID.</summary>
    public string? ExternalId { get; }

    /// <summary>Refers to an account by its ID.</summary>
    public static AccountRef ById(AccountId id) => new(id, null);

    /// <summary>Refers to an account by the host's customer ID.</summary>
    public static AccountRef ByExternalId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        return new AccountRef(null, externalId);
    }

    /// <summary>Refers to an account by its ID.</summary>
    public static AccountRef FromAccountId(AccountId id) => ById(id);

    /// <summary>Refers to an account by its ID.</summary>
    public static implicit operator AccountRef(AccountId id) => ById(id);

    /// <inheritdoc />
    public override string ToString() => Id?.ToString() ?? $"external:{ExternalId}";
}

/// <summary>Refers to an obligation in the current tenant, by ID, by the host's external ID, or by payment reference.</summary>
public sealed record ObligationRef
{
    private ObligationRef(ObligationId? id, string? externalId, string? paymentReference)
    {
        Id = id;
        ExternalId = externalId;
        PaymentReference = paymentReference;
    }

    /// <summary>The obligation ID, when referring by ID.</summary>
    public ObligationId? Id { get; }

    /// <summary>The host's ID for the loan or agreement, when referring by external ID.</summary>
    public string? ExternalId { get; }

    /// <summary>The payment reference, when referring by reference. Normalised with the tenant's format before lookup.</summary>
    public string? PaymentReference { get; }

    /// <summary>Refers to an obligation by its ID.</summary>
    public static ObligationRef ById(ObligationId id) => new(id, null, null);

    /// <summary>Refers to an obligation by the host's loan or agreement ID.</summary>
    public static ObligationRef ByExternalId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        return new ObligationRef(null, externalId, null);
    }

    /// <summary>Refers to an obligation by the payment reference customers quote, for example <c>LN-100001</c>.</summary>
    public static ObligationRef ByReference(string paymentReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentReference);
        return new ObligationRef(null, null, paymentReference);
    }

    /// <summary>Refers to an obligation by its ID.</summary>
    public static ObligationRef FromObligationId(ObligationId id) => ById(id);

    /// <summary>Refers to an obligation by its ID.</summary>
    public static implicit operator ObligationRef(ObligationId id) => ById(id);

    /// <inheritdoc />
    public override string ToString() =>
        Id?.ToString() ?? (ExternalId is not null ? $"external:{ExternalId}" : $"reference:{PaymentReference}");
}
