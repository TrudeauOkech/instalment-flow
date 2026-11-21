using InstalmentFlow.Verification;

namespace InstalmentFlow.Tests;

internal static class Invariants
{
    /// <summary>
    /// Asserts that every checked financial invariant holds for the tenant: the snapshot verifier always, and on
    /// PostgreSQL also the store's SQL verifier, so both implementations are checked against each other.
    /// </summary>
    public static void AssertHold(InstalmentFlowFactory factory, TenantId tenant)
    {
        var violations = LedgerVerifier.Verify(factory.Snapshot(tenant));
        Assert.True(violations.Count == 0, "Invariant violations:\n" + string.Join('\n', violations));
        if (TestEngine.IsPostgres)
        {
            var sql = factory.LedgerStore.VerifyAsync(tenant, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
            Assert.True(sql.Violations.Count == 0, "SQL verifier violations:\n" + string.Join('\n', sql.Violations));
        }
    }
}
