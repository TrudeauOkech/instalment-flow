using System.Net;
using InstalmentFlow.Payments;
using InstalmentFlow.Payments.Simulation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InstalmentFlow.AspNetCore;

/// <summary>Finds the tenant of an API request (ADR-007: there is no tenant-less engine).</summary>
public interface ITenantContext
{
    /// <summary>The request's tenant, or null if it has none.</summary>
    TenantId? Resolve(HttpContext context);
}

/// <summary>The tenant from a claim of the authenticated user, for example <c>tenant</c>.</summary>
/// <param name="claimType">The claim type.</param>
public sealed class ClaimsTenantContext(string claimType = "tenant") : ITenantContext
{
    /// <inheritdoc />
    public TenantId? Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.User.FindFirst(claimType)?.Value is { Length: > 0 } tenant ? TenantId.FromString(tenant) : null;
    }
}

/// <summary>The tenant from a route value, for example <c>/tenants/{tenant}/…</c>. Authorise the caller for it separately.</summary>
/// <param name="routeKey">The route value name.</param>
public sealed class RouteTenantContext(string routeKey = "tenant") : ITenantContext
{
    /// <inheritdoc />
    public TenantId? Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.RouteValues.TryGetValue(routeKey, out var value) && value is string { Length: > 0 } tenant ? TenantId.FromString(tenant) : null;
    }
}

/// <summary>A request needed a tenant and its tenant context found none.</summary>
public sealed class TenantNotResolvedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TenantNotResolvedException()
        : base("The request has no tenant.")
    {
    }
}

/// <summary>Callback endpoint settings (doc 03 §7.4).</summary>
public sealed class CallbackOptions
{
    /// <summary>The unguessable secret in the callback path (<c>/hooks/{secret}</c>); compared in constant time. Required.</summary>
    public string? PathSecret { get; set; }

    /// <summary>Callbacks larger than this are refused with 413. Default 64 KiB.</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>Source networks allowed per provider; a provider without an entry accepts any source.</summary>
    public IDictionary<string, IList<IPNetwork>> AllowedNetworks { get; } = new Dictionary<string, IList<IPNetwork>>(StringComparer.Ordinal);

    /// <summary>
    /// How long a validation answer may take before the default decision is sent. Default 5 seconds, well inside
    /// M-Pesa's roughly 8 second limit (R-I8).
    /// </summary>
    public TimeSpan ValidationBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Tenants that refuse, at validation, payments whose reference identifies no obligation. Others accept everything (ADR-002).</summary>
    public ISet<TenantId> RejectUnknownReferences { get; } = new HashSet<TenantId>();
}

/// <summary>Background worker settings.</summary>
public sealed class WorkerOptions
{
    /// <summary>Whether the workers run. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the inbox is processed. Default 2 seconds.</summary>
    public TimeSpan InboxInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many inbox workers run in this process. Default 8. Each records one payment at a time and is bound by
    /// database round trips, so one worker records about 190 payments per second; eight sustain NFR-4's 500 per second
    /// (see docs/performance.md). Workers claim entries under a lease, so they never record one twice, and events
    /// for one account may be processed out of order, as they may across several instances.
    /// </summary>
    public int InboxWorkers
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 8;

    /// <summary>How often due intents are checked. Default 15 seconds.</summary>
    public TimeSpan IntentInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often each provider account in <see cref="ReconcileAccounts"/> is reconciled. Default 1 hour (inside M-Pesa's 48 h Pull window).</summary>
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Provider accounts to reconcile, as (provider, account).</summary>
    public IList<(string Provider, string ProviderAccount)> ReconcileAccounts { get; } = [];

    internal List<(string Name, TimeSpan Interval, Func<IServiceProvider, CancellationToken, Task> Work)> Custom { get; } = [];

    /// <summary>Runs <paramref name="work"/> every <paramref name="interval"/>, for example the PostgreSQL outbox dispatcher.</summary>
    public WorkerOptions Add(string name, TimeSpan interval, Func<IServiceProvider, CancellationToken, Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        Custom.Add((name, interval, work));
        return this;
    }
}

/// <summary>Health check thresholds.</summary>
public sealed class HealthOptions
{
    /// <summary>The age of the oldest unprocessed inbox entry above which the inbox is degraded. Default 5 minutes.</summary>
    public TimeSpan InboxBacklog { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The age of the oldest ambiguous intent above which intents are degraded. Default 1 hour.</summary>
    public TimeSpan AmbiguousIntentAge { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The age of the last reconciliation above which an account is unhealthy. Default 3 hours.</summary>
    public TimeSpan ReconciliationAge { get; set; } = TimeSpan.FromHours(3);

    /// <summary>The outbox lag above which the outbox is degraded. Default 5 minutes.</summary>
    public TimeSpan OutboxLag { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Measures outbox lag, for example <c>OutboxDispatcher.GetLagAsync</c>; without it, the outbox is not checked.</summary>
    public Func<IServiceProvider, CancellationToken, Task<TimeSpan?>>? MeasureOutboxLag { get; set; }
}

/// <summary>Configures InstalmentFlow in an ASP.NET Core host.</summary>
public sealed class InstalmentFlowServerBuilder
{
    internal InstalmentFlowServerBuilder()
    {
    }

    /// <summary>Callback endpoint settings.</summary>
    public CallbackOptions Callbacks { get; } = new();

    /// <summary>Background workers.</summary>
    public WorkerOptions Workers { get; } = new();

    /// <summary>Health check thresholds.</summary>
    public HealthOptions Health { get; } = new();

    internal Func<IServiceProvider, CancellationToken, Task<InstalmentFlowFactory>>? Engine { get; private set; }

    internal Func<IServiceProvider, InstalmentFlowFactory, CancellationToken, Task<InstalmentFlowPayments>>? Payments { get; private set; }

    internal SimulatedProvider? Simulator { get; private set; }

    internal bool SimulatorOutsideDevelopment { get; private set; }

    /// <summary>
    /// Creates the engine at start-up, for example
    /// <c>(sp, ct) =&gt; InstalmentFlowPostgres.CreateFactoryAsync(options, configure, ct)</c>.
    /// </summary>
    public InstalmentFlowServerBuilder UseEngine(Func<IServiceProvider, CancellationToken, Task<InstalmentFlowFactory>> create)
    {
        Engine = create ?? throw new ArgumentNullException(nameof(create));
        return this;
    }

    /// <summary>An in-memory engine, for development and tests.</summary>
    public InstalmentFlowServerBuilder UseInMemoryEngine(Action<InstalmentFlowBuilder> configure) =>
        UseEngine((_, _) => Task.FromResult(InstalmentFlowFactory.CreateInMemory(configure)));

    /// <summary>
    /// Creates the payments package at start-up, for example
    /// <c>(sp, flows, ct) =&gt; InstalmentFlowPostgres.CreatePaymentsAsync(options, flows, p =&gt; p.AddProvider(mpesa), ct)</c>.
    /// </summary>
    public InstalmentFlowServerBuilder UsePayments(Func<IServiceProvider, InstalmentFlowFactory, CancellationToken, Task<InstalmentFlowPayments>> create)
    {
        Payments = create ?? throw new ArgumentNullException(nameof(create));
        return this;
    }

    /// <summary>Payments with their state in memory, for development and tests.</summary>
    public InstalmentFlowServerBuilder UseInMemoryPayments(Action<IServiceProvider, PaymentsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return UsePayments((sp, flows, _) => Task.FromResult(InstalmentFlowPayments.CreateInMemory(flows, p => configure(sp, p))));
    }

    /// <summary>
    /// Makes the simulated provider's endpoints available (<c>MapInstalmentFlowSimulator</c>) and connects it to the
    /// inbox. Add it to the payments as a provider too. Its endpoints are unauthenticated and create payments, so they
    /// are refused outside the <c>Development</c> environment unless <paramref name="allowOutsideDevelopment"/> (ADR-016).
    /// </summary>
    public InstalmentFlowServerBuilder UseSimulator(SimulatedProvider simulator, bool allowOutsideDevelopment = false)
    {
        Simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        SimulatorOutsideDevelopment = allowOutsideDevelopment;
        return this;
    }
}

/// <summary>Registers InstalmentFlow with dependency injection.</summary>
public static class InstalmentFlowServiceCollectionExtensions
{
    /// <summary>
    /// Adds the engine and, when configured, payments: both created once at start-up. Requests get an
    /// <see cref="IInstalmentFlow"/> for their tenant from the registered <see cref="ITenantContext"/> (by default the
    /// <c>tenant</c> claim). Also maps engine errors to <c>ProblemDetails</c> (with <c>app.UseExceptionHandler()</c>).
    /// </summary>
    public static IServiceCollection AddInstalmentFlow(this IServiceCollection services, Action<InstalmentFlowServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new InstalmentFlowServerBuilder();
        configure(builder);
        if (builder.Engine is null)
        {
            throw new InvalidOperationException("Configure the engine with UseEngine or UseInMemoryEngine.");
        }

        services.AddSingleton(builder);
        services.AddSingleton<InstalmentFlowRuntime>();
        services.AddHostedService<InstalmentFlowStartup>();
        services.AddHostedService<InstalmentFlowWorkers>();
        services.TryAddSingleton<ITenantContext>(new ClaimsTenantContext());
        services.AddHttpContextAccessor();
        services.AddSingleton(sp => sp.GetRequiredService<InstalmentFlowRuntime>().Flows);
        services.AddSingleton(sp => sp.GetRequiredService<InstalmentFlowRuntime>().Payments);
        services.AddScoped(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext ?? throw new TenantNotResolvedException();
            var tenant = sp.GetRequiredService<ITenantContext>().Resolve(http) ?? throw new TenantNotResolvedException();
            return sp.GetRequiredService<InstalmentFlowRuntime>().Flows.ForTenant(tenant);
        });
        services.AddProblemDetails();
        services.AddExceptionHandler<InstalmentFlowExceptionHandler>();
        return services;
    }

    /// <summary>Uses <typeparamref name="T"/> to find each request's tenant.</summary>
    public static IServiceCollection UseTenantContext<T>(this IServiceCollection services)
        where T : class, ITenantContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<ITenantContext, T>());
        return services;
    }
}

/// <summary>The engine and payments of the host, created by <see cref="InstalmentFlowStartup"/> before requests are served.</summary>
internal sealed class InstalmentFlowRuntime(IServiceProvider services, InstalmentFlowServerBuilder builder)
{
    private InstalmentFlowFactory? _flows;
    private InstalmentFlowPayments? _payments;

    public InstalmentFlowServerBuilder Settings => builder;

    public InstalmentFlowFactory Flows => _flows ?? throw new InvalidOperationException("InstalmentFlow has not started yet.");

    public InstalmentFlowPayments Payments =>
        _payments ?? throw new InvalidOperationException(_flows is null ? "InstalmentFlow has not started yet." : "Payments are not configured (UsePayments).");

    public bool HasPayments => _payments is not null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_flows is not null)
        {
            return;
        }

        var flows = await builder.Engine!(services, cancellationToken).ConfigureAwait(false);
        if (builder.Payments is { } create)
        {
            _payments = await create(services, flows, cancellationToken).ConfigureAwait(false);
            builder.Simulator?.ConnectTo(_payments.Inbox);
        }

        _flows = flows;
    }
}

internal sealed partial class InstalmentFlowStartup(InstalmentFlowRuntime runtime, IHostEnvironment environment, ILogger<InstalmentFlowStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!runtime.HasPayments || environment.IsDevelopment())
        {
            return;
        }

        // A provider whose callbacks cannot be authenticated relies on the path secret and the source network (R-I3).
        foreach (var provider in runtime.Payments.Providers.Where(p => !p.Capabilities.HasFlag(ProviderCapabilities.AuthenticatedCallbacks)))
        {
            if (!runtime.Settings.Callbacks.AllowedNetworks.TryGetValue(provider.Provider, out var networks) || networks.Count == 0)
            {
                UnprotectedProvider(provider.Provider);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Callbacks from provider {Provider} cannot be authenticated and no source networks are allowed for it: anyone with the path secret can post payments. Set Callbacks.AllowedNetworks (behind a proxy, with forwarded headers) and consider ConfirmByStatementAbove.")]
    private partial void UnprotectedProvider(string provider);
}

/// <summary>Maps engine errors to <c>ProblemDetails</c> with a stable <c>errorCode</c> extension.</summary>
internal sealed class InstalmentFlowExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    private static readonly HashSet<string> Conflicts = new(StringComparer.Ordinal)
    {
        ErrorCodes.IdempotencyKeyReused, ErrorCodes.DuplicatePaymentReference, ErrorCodes.PaymentConflict, ErrorCodes.DuplicateExternalId,
        PaymentsErrorCodes.IntentKeyReused, PaymentsErrorCodes.ProviderAccountTaken,
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code) = exception switch
        {
            TenantNotResolvedException => (StatusCodes.Status403Forbidden, "No tenant", null),
            NotFoundException e => (StatusCodes.Status404NotFound, "Not found", e.ErrorCode),
            UnknownTenantException e => (StatusCodes.Status404NotFound, "Unknown tenant", e.ErrorCode),
            ConcurrencyConflictException e => (StatusCodes.Status409Conflict, "Concurrent change", e.ErrorCode),
            InstalmentFlowException e when Conflicts.Contains(e.ErrorCode) => (StatusCodes.Status409Conflict, "Conflict", e.ErrorCode),
            InstalmentFlowException e => (StatusCodes.Status422UnprocessableEntity, "Rule violated", e.ErrorCode),
            _ => (0, null, null),
        };
        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        var details = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = status, Title = title, Detail = exception.Message };
        if (code is not null)
        {
            details.Extensions["errorCode"] = code;
        }

        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = details, Exception = exception }).ConfigureAwait(false);
    }
}
