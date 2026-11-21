using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InstalmentFlow.Storage;

namespace InstalmentFlow.Engine;

/// <summary>A planned command: what to commit, and the result to return once committed.</summary>
internal sealed record Planned<TResult>(Commit Commit, TResult Result);

/// <summary>
/// A canonical, unambiguous description of a request's meaningful content, hashed to detect an idempotency key
/// reused for a different request (invariant I-4). Each field is length-prefixed, so values cannot run together.
/// </summary>
internal sealed class Fingerprint
{
    private readonly StringBuilder _text = new();

    public Fingerprint(string commandType) => Add("command", commandType);

    public Fingerprint Add(string name, string? value)
    {
        _text.Append(name).Append('=');
        if (value is null)
        {
            _text.Append('-');
        }
        else
        {
            _text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        _text.Append('\n');
        return this;
    }

    public Fingerprint Add(string name, Money? value) =>
        Add(name, value is null ? null : $"{value.Currency.Code}:{value.MinorUnitAmount.ToString("0", CultureInfo.InvariantCulture)}");

    public Fingerprint Add(string name, DateTimeOffset value) => Add(name, value.UtcTicks.ToString(CultureInfo.InvariantCulture));

    public Fingerprint Add(string name, DateOnly value) => Add(name, value.DayNumber.ToString(CultureInfo.InvariantCulture));

    public Fingerprint Add(string name, long value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

    public ImmutableArray<byte> Hash() => [.. SHA256.HashData(Encoding.UTF8.GetBytes(_text.ToString()))];
}

/// <summary>
/// Runs a command: replays a stored result for a repeated idempotency key, rejects a key reused for a different
/// request (<c>IF1002</c>), and otherwise plans and commits atomically, retrying when another command for the
/// same aggregate commits first.
/// </summary>
internal sealed class CommandRunner(TenantContext context)
{
    public const int DefaultMaxAttempts = 10;
    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromMilliseconds(50);

    public async Task<TResult> RunAsync<TResult>(
        InstalmentFlowRequest request,
        string commandType,
        Func<TenantSnapshot, CancellationToken, Task<Fingerprint>> fingerprint,
        Func<TenantSnapshot, CancellationToken, Task<Planned<TResult>>> plan,
        Func<TResult, TResult> onReplay,
        CancellationToken cancellationToken)
        where TResult : class
    {
        var key = Validate.Request(request).Value;
        ConcurrencyConflictException? conflict = null;

        var maxAttempts = context.Services.MaxAttempts;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var tenant = await context.LoadAsync(cancellationToken).ConfigureAwait(false);
            var hash = (await fingerprint(tenant, cancellationToken).ConfigureAwait(false)).Hash();

            var existing = await context.Store.GetIdempotencyAsync(context.TenantId, key, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return Replay(existing, commandType, hash, onReplay);
            }

            var planned = await plan(tenant, cancellationToken).ConfigureAwait(false);
            var commit = planned.Commit with
            {
                Idempotency = new IdempotencyRecord(key, commandType, hash, planned.Result, tenant.Clock.UtcNow),
            };

            try
            {
                await context.Store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
                return planned.Result;
            }
            catch (ConcurrencyConflictException ex)
            {
                conflict = ex;
            }
            catch (IdempotencyKeyTakenException)
            {
                // A concurrent request with the same key won; the next attempt replays its result.
            }
            catch (DuplicatePaymentException)
            {
                // A concurrent request recorded the same provider reference; the next attempt reports a duplicate.
            }
            catch (DuplicateReversalException)
            {
                // A concurrent request recorded the same reversal reference; the next attempt reports a duplicate.
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw conflict ?? new ConcurrencyConflictException(default, Domain.CustomerAccount.NewVersion);
    }

    /// <summary>Jittered exponential backoff (1, 2, 4… ms, capped), so contending commands spread out instead of colliding again.</summary>
    private static TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(s_maxBackoff.TotalMilliseconds, Math.Pow(2, attempt - 1)); // floating-point-allowed: retry timing, not money
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling); // floating-point-allowed: retry timing, not money
    }

    private static TResult Replay<TResult>(IdempotencyRecord existing, string commandType, ImmutableArray<byte> hash, Func<TResult, TResult> onReplay)
        where TResult : class
    {
        if (!string.Equals(existing.CommandType, commandType, StringComparison.Ordinal) || !existing.PayloadHash.SequenceEqual(hash) ||
            existing.Result is not TResult result)
        {
            throw new RuleViolationException(
                ErrorCodes.IdempotencyKeyReused,
                $"Idempotency key '{existing.Key}' was already used for a different request ({existing.CommandType}).");
        }

        return onReplay(result);
    }
}
