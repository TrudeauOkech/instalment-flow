using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InstalmentFlow.Payments;
using InstalmentFlow.Payments.Simulation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InstalmentFlow.AspNetCore;

/// <summary>Maps provider callback and simulator endpoints.</summary>
public static class InstalmentFlowEndpoints
{
    private const string Secret = "secret";

    /// <summary>
    /// Maps provider callbacks under <paramref name="prefix"/>, which must contain <c>{secret}</c>:
    /// <list type="bullet">
    /// <item><c>POST {prefix}/{provider}/{kind}</c> and <c>POST {prefix}/{provider}/{kind}/{account}</c> store the callback in
    /// the inbox and answer the provider once it is stored (doc 03 §6). The account segment is for providers whose
    /// callbacks do not name their merchant account (M-Pesa STK).</item>
    /// <item><c>POST {prefix}/{provider}/validate</c> answers a validation request within the validation budget.</item>
    /// </list>
    /// Each request must carry the path secret (otherwise 404) and come from an allowed network (otherwise 403).
    /// Tenants are resolved from the provider account by the payments package's router.
    /// </summary>
    public static RouteGroupBuilder MapInstalmentFlowCallbacks(this IEndpointRouteBuilder endpoints, string prefix = "/hooks/{secret}")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!prefix.Contains("{" + Secret + "}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The callback prefix must contain {secret}.", nameof(prefix));
        }

        var settings = endpoints.ServiceProvider.GetRequiredService<InstalmentFlowServerBuilder>();
        if (string.IsNullOrWhiteSpace(settings.Callbacks.PathSecret) || settings.Callbacks.PathSecret.Length < 16)
        {
            throw new InvalidOperationException("Set Callbacks.PathSecret to an unguessable value of at least 16 characters.");
        }

        var group = endpoints.MapGroup(prefix);
        group.MapPost("/{provider}/validate", ValidateAsync).DisableAntiforgery();
        group.MapPost("/{provider}/validate/{account}", ValidateAsync).DisableAntiforgery();
        group.MapPost("/{provider}/{kind}", ReceiveAsync).DisableAntiforgery();
        group.MapPost("/{provider}/{kind}/{account}", ReceiveAsync).DisableAntiforgery();
        return group;
    }

    /// <summary>
    /// Maps endpoints that drive the simulated provider, for front-end development: <c>POST {prefix}/pay</c>,
    /// <c>POST {prefix}/push</c> (how the next push is answered), <c>POST {prefix}/deliver</c> and
    /// <c>GET {prefix}/transactions</c>. Refused in <c>Production</c> unless the host allowed it (ADR-016).
    /// </summary>
    public static RouteGroupBuilder MapInstalmentFlowSimulator(this IEndpointRouteBuilder endpoints, string prefix = "/simulator")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var settings = endpoints.ServiceProvider.GetRequiredService<InstalmentFlowServerBuilder>();
        var simulator = settings.Simulator ?? throw new InvalidOperationException("Register the simulator with UseSimulator first.");
        var environment = endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        if (environment.IsProduction() && !settings.SimulatorInProduction)
        {
            throw new InvalidOperationException("The simulated provider is refused in Production; pass allowInProduction to UseSimulator to override.");
        }

        var group = endpoints.MapGroup(prefix);
        group.MapPost("/pay", (SimulatedPaymentDto payment) => Results.Ok(simulator.Pay(new SimulatedPayment
        {
            ProviderAccount = payment.Account, Amount = Money.Of(payment.Amount, payment.Currency ?? "KES"), AccountReference = payment.Reference,
            Payer = payment.Payer ?? "2547****0000", Faults = Faults(payment.Faults), CallbackDelay = TimeSpan.FromSeconds(payment.DelaySeconds),
        }))).DisableAntiforgery();
        group.MapPost("/push", (SimulatedPushDto push) =>
        {
            simulator.NextPush(new SimulatedPush
            {
                Outcome = Enum.Parse<SimulatedPushOutcome>(push.Outcome ?? "Approve", ignoreCase: true), Faults = Faults(push.Faults),
                AnswerAfter = TimeSpan.FromSeconds(push.AnswerAfterSeconds), CallbackDelay = TimeSpan.FromSeconds(push.DelaySeconds),
            });
            return Results.NoContent();
        }).DisableAntiforgery();
        group.MapPost("/deliver", async (CancellationToken ct) => Results.Ok(new { delivered = await simulator.DeliverDueAsync(ct).ConfigureAwait(false) })).DisableAntiforgery();
        group.MapGet("/transactions", () => Results.Ok(simulator.Transactions));
        return group;
    }

    private static async Task<IResult> ReceiveAsync(HttpContext http, string provider, string kind, string? account, CancellationToken ct)
    {
        var runtime = http.RequestServices.GetRequiredService<InstalmentFlowRuntime>();
        var (body, refusal) = await GuardAsync(http, runtime.Settings.Callbacks, provider, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var adapter = runtime.Payments.FindProvider(provider);
        if (adapter is null)
        {
            return Results.NotFound();
        }

        var headers = http.Request.Headers
            .Where(h => !string.Equals(h.Key, "Cookie", StringComparison.OrdinalIgnoreCase) && !string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var callback = new ProviderCallback(provider, kind, body, headers, account);
        var receipt = await runtime.Payments.Inbox.ReceiveAsync(callback, ct).ConfigureAwait(false);
        var reply = adapter is IProviderReplies replies
            ? replies.Acknowledge(callback, receipt)
            : receipt.Outcome == InboxReceiptOutcome.Rejected ? ProviderReply.Json(400, """{"status":"rejected"}""") : ProviderReply.Json(200, """{"status":"accepted"}""");
        return new ReplyResult(reply);
    }

    /// <summary>
    /// A read-only check, answered within the budget: does the reference identify an obligation of the routed tenant?
    /// Only tenants that opted in refuse unknown references; on any doubt, the default (accept) is sent.
    /// </summary>
    private static async Task<IResult> ValidateAsync(HttpContext http, string provider, string? account, CancellationToken ct)
    {
        var runtime = http.RequestServices.GetRequiredService<InstalmentFlowRuntime>();
        var options = runtime.Settings.Callbacks;
        var (body, refusal) = await GuardAsync(http, options, provider, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (runtime.Payments.FindProvider(provider) is not IPaymentValidation validation)
        {
            return Results.NotFound();
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.ValidationBudget);
        var decision = ValidationDecision.Accept;
        try
        {
            decision = await DecideAsync(runtime, options, validation.ParseValidation(new ProviderCallback(provider, "validate", body, providerAccount: account)), provider, budget.Token)
                .WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Out of time: the default decision goes out before the provider gives up.
        }

        var reply = validation.Reply(decision);
        return new ReplyResult(reply);
    }

    private static async Task<ValidationDecision> DecideAsync(InstalmentFlowRuntime runtime, CallbackOptions options, PaymentValidationRequest? request, string provider, CancellationToken ct)
    {
        if (request is null)
        {
            return ValidationDecision.Accept;
        }

        var preview = NormalisedPaymentEvent.PaymentReceived(provider, request.ProviderAccount, request.TransactionId, request.Amount, DateTimeOffset.UnixEpoch, request.AccountReference);
        if (await runtime.Payments.RouteAsync(preview, ct).ConfigureAwait(false) is not { } tenant || !options.RejectUnknownReferences.Contains(tenant))
        {
            return ValidationDecision.Accept;
        }

        if (string.IsNullOrWhiteSpace(request.AccountReference))
        {
            return ValidationDecision.RejectUnknownReference;
        }

        try
        {
            var obligation = await runtime.Flows.ForTenant(tenant).Queries.GetObligationAsync(ObligationRef.ByReference(request.AccountReference), cancellationToken: ct).ConfigureAwait(false);
            return obligation.Status == ObligationStatus.Active ? ValidationDecision.Accept : ValidationDecision.RejectUnknownReference;
        }
        catch (NotFoundException)
        {
            return ValidationDecision.RejectUnknownReference;
        }
        catch (RuleViolationException)
        {
            return ValidationDecision.RejectUnknownReference;   // a reference that is not even well formed
        }
    }

    /// <summary>The path secret, the source network and the size limit, in that order; then the body.</summary>
    private static async Task<(byte[] Body, IResult? Refusal)> GuardAsync(HttpContext http, CallbackOptions options, string provider, CancellationToken ct)
    {
        var given = Encoding.UTF8.GetBytes(http.Request.RouteValues.TryGetValue(Secret, out var value) ? value as string ?? string.Empty : string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(options.PathSecret!)))
        {
            return ([], Results.NotFound());
        }

        if (options.AllowedNetworks.TryGetValue(provider, out var networks) && networks.Count > 0)
        {
            var remote = http.Connection.RemoteIpAddress;
            if (remote is null || !networks.Any(n => n.Contains(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)))
            {
                return ([], Results.StatusCode(StatusCodes.Status403Forbidden));
            }
        }

        if (http.Request.ContentLength > options.MaxBodyBytes)
        {
            return ([], Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > options.MaxBodyBytes)
            {
                return ([], Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), null);
    }

    private static SimulatedFaults Faults(string? faults) =>
        string.IsNullOrWhiteSpace(faults)
            ? SimulatedFaults.None
            : faults.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Aggregate(SimulatedFaults.None, (all, f) => all | Enum.Parse<SimulatedFaults>(f, ignoreCase: true));

    /// <summary>A simulated customer payment.</summary>
    /// <param name="Account">The merchant account paid.</param>
    /// <param name="Amount">The amount.</param>
    /// <param name="Reference">The account reference typed.</param>
    /// <param name="Currency">The currency; default KES.</param>
    /// <param name="Payer">The payer.</param>
    /// <param name="Faults">Comma-separated <see cref="SimulatedFaults"/>.</param>
    /// <param name="DelaySeconds">Callback delay.</param>
    internal sealed record SimulatedPaymentDto(string Account, decimal Amount, string? Reference, string? Currency, string? Payer, string? Faults, int DelaySeconds);

    /// <summary>How the next simulated push is answered.</summary>
    /// <param name="Outcome">A <see cref="SimulatedPushOutcome"/>.</param>
    /// <param name="Faults">Comma-separated <see cref="SimulatedFaults"/>.</param>
    /// <param name="AnswerAfterSeconds">How long the customer takes.</param>
    /// <param name="DelaySeconds">Callback delay.</param>
    internal sealed record SimulatedPushDto(string? Outcome, string? Faults, int AnswerAfterSeconds, int DelaySeconds);

    internal static string Format(TimeSpan age) => (age.Ticks / TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture) + " min";

    /// <summary>Writes a provider's reply as given: status, media type and body.</summary>
    private sealed class ReplyResult(ProviderReply reply) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = reply.StatusCode;
            httpContext.Response.ContentType = reply.ContentType;
            await httpContext.Response.Body.WriteAsync(reply.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
