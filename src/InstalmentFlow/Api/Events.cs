namespace InstalmentFlow;

/// <summary>
/// A domain event delivered to the host through the transactional outbox (doc 02 §8): written in the same
/// transaction as the change it describes, then published at least once. Events of one customer account are
/// published in the order they were committed; consumers deduplicate by <see cref="EventId"/>.
/// </summary>
/// <param name="EventId">A unique, time-ordered ID.</param>
/// <param name="Type">The event type, for example <c>PaymentReceived</c> or <c>InstalmentSettled</c>. Values are stable.</param>
/// <param name="Tenant">The tenant.</param>
/// <param name="AccountId">The customer account, if the event belongs to one.</param>
/// <param name="OccurredAt">When the change was committed.</param>
/// <param name="Data">The event's fields as invariant-culture strings, for example <c>paymentId</c> and <c>amount</c>.</param>
public sealed record IntegrationEvent(
    Guid EventId,
    string Type,
    TenantId Tenant,
    AccountId? AccountId,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Delivers outbox events to the host's message broker, webhook or handler. Implementations must be idempotent per
/// <see cref="IntegrationEvent.EventId"/>: an event can be delivered again after a crash or a timeout.
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>Publishes one event. Throwing leaves it in the outbox for a later attempt.</summary>
    Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken);
}
