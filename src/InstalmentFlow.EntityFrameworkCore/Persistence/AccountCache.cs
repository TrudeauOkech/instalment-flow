using System.Collections.Concurrent;
using InstalmentFlow.Domain;

namespace InstalmentFlow.EntityFrameworkCore.Persistence;

/// <summary>
/// Customer accounts as last committed or loaded, keyed by tenant and account. Every change to an account bumps its
/// version (doc 05 §5), so a cached copy is used only after a one-row check that its version is still current: a hit
/// avoids reloading thousands of schedule rows. Bounded: when full, a tenth of the entries are dropped.
/// Only committed state is ever cached (never anything read or written inside a host transaction), so an entry is
/// correct for its version, and a newer entry is never replaced by an older one.
/// </summary>
internal sealed class AccountCache(int capacity)
{
    private readonly ConcurrentDictionary<(string Tenant, Guid Account), CustomerAccount> _accounts = new();

    public bool TryGet(TenantId tenant, AccountId account, out CustomerAccount cached) =>
        _accounts.TryGetValue((tenant.Value, account.Value), out cached!);

    public void Set(CustomerAccount account)
    {
        if (_accounts.Count >= capacity)
        {
            foreach (var key in _accounts.Keys.Take(Math.Max(1, capacity / 10)).ToList())
            {
                _accounts.TryRemove(key, out _);
            }
        }

        // A slow load of an older version must not replace what a later commit cached.
        _accounts.AddOrUpdate((account.TenantId.Value, account.Id.Value), account, (_, existing) => existing.Version > account.Version ? existing : account);
    }

    /// <summary>
    /// Drops the entry if it is no newer than <paramref name="version"/>. A command that lost a race keeps the
    /// winner's newer entry, so the retry (and every later command on the account) does not reload it.
    /// </summary>
    public void RemoveUpTo(TenantId tenant, AccountId account, long version)
    {
        var key = (tenant.Value, account.Value);
        if (_accounts.TryGetValue(key, out var existing) && existing.Version <= version)
        {
            _accounts.TryRemove(KeyValuePair.Create(key, existing));
        }
    }

    /// <summary>
    /// Whether a reload of the committed account would list its lots in the same order: lots are reloaded in insertion
    /// order, so every lot must have been there before (in the same order) or be the lot of a payment this commit
    /// created, after them. A lot moved in from unidentified funds keeps its older position, so such accounts are not cached.
    /// </summary>
    public static bool ReloadsIdentically(CustomerAccount? before, CustomerAccount after, IReadOnlySet<PaymentId> created)
    {
        var old = before?.Lots.Select(l => l.PaymentId).ToList() ?? [];
        var kept = after.Lots.Select(l => l.PaymentId).Where(id => !created.Contains(id)).ToList();
        if (!kept.SequenceEqual(old))
        {
            return false;
        }

        var firstNew = after.Lots.Select((l, i) => (l, i)).FirstOrDefault(x => created.Contains(x.l.PaymentId), (default!, after.Lots.Length)).Item2;
        return after.Lots.Skip(firstNew).All(l => created.Contains(l.PaymentId));
    }
}
