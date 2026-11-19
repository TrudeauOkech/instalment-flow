using InstalmentFlow.Verification;

namespace InstalmentFlow.Tests;

internal static class Invariants
{
    /// <summary>Asserts that every checked financial invariant holds for the tenant.</summary>
    public static void AssertHold(InstalmentFlowFactory factory, TenantId tenant)
    {
        var violations = LedgerVerifier.Verify(factory.InMemoryStore!.Snapshot(tenant));
        Assert.True(violations.Count == 0, "Invariant violations:\n" + string.Join('\n', violations));
    }
}
