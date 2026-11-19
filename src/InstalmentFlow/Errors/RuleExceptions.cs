namespace InstalmentFlow;

/// <summary>
/// A request broke a business rule. <see cref="InstalmentFlowException.ErrorCode"/> identifies which one;
/// see <see cref="ErrorCodes"/>.
/// </summary>
public sealed class RuleViolationException : InstalmentFlowException
{
    /// <summary>Initialises the exception with a stable error code and a message.</summary>
    public RuleViolationException(string errorCode, string message)
        : base(errorCode, message)
    {
    }
}

/// <summary>The referenced entity does not exist in the tenant (<c>IF1010</c>).</summary>
public sealed class NotFoundException : InstalmentFlowException
{
    /// <summary>Initialises the exception for an entity kind and the reference that was not found.</summary>
    public NotFoundException(string entity, string reference)
        : base(ErrorCodes.NotFound, $"{entity} '{reference}' was not found.")
    {
        Entity = entity;
        Reference = reference;
    }

    /// <summary>The kind of entity, for example <c>Obligation</c>.</summary>
    public string Entity { get; }

    /// <summary>The reference that was looked up.</summary>
    public string Reference { get; }
}

/// <summary>The account changed concurrently; reload and retry (<c>IF3001</c>).</summary>
public sealed class ConcurrencyConflictException : InstalmentFlowException
{
    /// <summary>Initialises the exception for the account that changed.</summary>
    public ConcurrencyConflictException(AccountId accountId, long expectedVersion)
        : base(ErrorCodes.ConcurrencyConflict, $"Account {accountId} changed concurrently (expected version {expectedVersion}).")
    {
        AccountId = accountId;
        ExpectedVersion = expectedVersion;
    }

    /// <summary>The account that changed.</summary>
    public AccountId AccountId { get; }

    /// <summary>The version the command was based on.</summary>
    public long ExpectedVersion { get; }
}

/// <summary>The tenant does not exist (<c>IF5002</c>).</summary>
public sealed class UnknownTenantException : InstalmentFlowException
{
    /// <summary>Initialises the exception for the unknown tenant.</summary>
    public UnknownTenantException(TenantId tenantId)
        : base(ErrorCodes.UnknownTenant, $"Tenant '{tenantId}' does not exist.")
    {
        TenantId = tenantId;
    }

    /// <summary>The tenant that was requested.</summary>
    public TenantId TenantId { get; }
}
