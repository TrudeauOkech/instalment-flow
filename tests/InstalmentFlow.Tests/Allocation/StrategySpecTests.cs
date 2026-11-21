using System.Collections.Immutable;
using System.Globalization;
using InstalmentFlow.Domain;
using InstalmentFlow.Ledger;

namespace InstalmentFlow.Tests;

/// <summary>
/// Runs every <c>allocation-spec</c> block in <c>docs/allocation-strategies/*.md</c> against the allocation engine,
/// so the strategy documentation is also the strategy test suite.
/// </summary>
public sealed class StrategySpecTests
{
    private static readonly TenantId s_tenant = "spec";
    private static readonly DateTimeOffset s_epoch = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Specs
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (name, _) in LoadSpecs())
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Fact]
    public void Every_strategy_page_has_at_least_one_spec()
    {
        var pages = LoadSpecs().Select(s => s.Name.Split('#')[0]).Distinct().ToList();

        Assert.Equal(
            ["charges-first.md", "current-due-first.md", "directed.md", "future-instalments.md", "oldest-due-first.md", "principal-first.md", "proportional.md"],
            pages.Order(StringComparer.Ordinal));
        Assert.All(AllocationStrategies.All, s => Assert.Contains($"{s.Ref.Name}.md", pages));
    }

    [Theory]
    [MemberData(nameof(Specs))]
    public void Documented_example_holds(string name)
    {
        var spec = Parse(LoadSpecs().Single(s => s.Name == name).Body);

        var result = AllocationEngine.Allocate(
            spec.Account, spec.Lot, spec.Eligible, spec.Strategy, spec.Policy, policyVersion: 1, spec.AsOf,
            new JournalTxId(Guid.CreateVersion7()), s_epoch, TimeProvider.System);

        var actual = result.Allocations
            .Select(a => $"{spec.Labels[a.ObligationId]}#{a.InstalmentSequence} {ComponentText(a.Component)} {a.Amount.Amount.ToString("0.##", CultureInfo.InvariantCulture)}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(spec.Expected.Order(StringComparer.Ordinal), actual);
        Assert.Equal(spec.ExpectedUnapplied, result.Account.Unapplied(Currency.KES));
    }

    private static IEnumerable<(string Name, string Body)> LoadSpecs()
    {
        var directory = Path.Combine(RepositoryRoot(), "docs", "allocation-strategies");
        foreach (var file in Directory.GetFiles(directory, "*.md").Where(f => Path.GetFileName(f) != "README.md").Order(StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(file);
            var index = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() != "```allocation-spec")
                {
                    continue;
                }

                var end = Array.FindIndex(lines, i + 1, l => l.TrimEnd() == "```");
                yield return ($"{Path.GetFileName(file)}#{++index}", string.Join('\n', lines[(i + 1)..end]));
                i = end;
            }
        }
    }

    private static Spec Parse(string body)
    {
        IAllocationStrategy? strategy = null;
        var policy = TenantPolicy.Default;
        DateOnly? asOf = null;
        Money? pay = null;
        var eligible = new List<string>();
        var obligations = new List<(string Label, int Opened, List<PlannedInstalment> Instalments)>();
        var expected = new List<string>();
        Money? unapplied = null;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Split('#', 2) is var parts && raw.TrimStart().StartsWith("expect", StringComparison.Ordinal) ? raw : parts[0];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (char.IsWhiteSpace(raw[0]))
            {
                // An instalment of the current obligation: "<date> <component> <amount> ...".
                var amounts = new List<Component>();
                for (var i = 1; i < words.Length; i += 2)
                {
                    amounts.Add(new Component(ParseComponent(words[i]), Kes(words[i + 1])));
                }

                obligations[^1].Instalments.Add(new PlannedInstalment(DateOnly.Parse(words[0], CultureInfo.InvariantCulture), amounts));
                continue;
            }

            switch (words[0])
            {
                case "strategy":
                    var at = words[1].Split('@');
                    strategy = AllocationStrategies.All.Single(s => s.Ref == new StrategyRef(at[0], int.Parse(at[1], CultureInfo.InvariantCulture)));
                    policy = policy with { Allocation = strategy.Ref };
                    break;
                case "components":
                    policy = policy with { ComponentOrder = [.. words[1].Split(',').Select(c => Enum.Parse<ComponentType>(c, ignoreCase: true))] };
                    break;
                case "future":
                    policy = policy with
                    {
                        FutureInstalments = words[1] switch
                        {
                            "hold" => FutureInstalmentPolicy.HoldAsCredit,
                            "next" => FutureInstalmentPolicy.ApplyToNextInstalments,
                            "prepay" => FutureInstalmentPolicy.PrepayPrincipal,
                            _ => throw new FormatException($"Unknown future policy '{words[1]}'."),
                        },
                    };
                    break;
                case "as-of":
                    asOf = DateOnly.Parse(words[1], CultureInfo.InvariantCulture);
                    break;
                case "pay":
                    pay = Kes(words[1]);
                    break;
                case "eligible":
                    eligible.AddRange(words.Skip(1));
                    break;
                case "obligation":
                    obligations.Add((words[1], int.Parse(words[3], CultureInfo.InvariantCulture), []));
                    break;
                case "expect" when words[1] == "unapplied":
                    unapplied = Kes(words[2]);
                    break;
                case "expect":
                    expected.Add($"{words[1]} {words[2]} {decimal.Parse(words[3], CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture)}");
                    break;
                default:
                    throw new FormatException($"Unknown spec line '{line}'.");
            }
        }

        var accountId = new AccountId(Guid.CreateVersion7());
        var paymentId = new PaymentId(Guid.CreateVersion7());
        var labels = new Dictionary<ObligationId, string>();
        var built = ImmutableArray.CreateBuilder<Obligation>();
        foreach (var (label, opened, instalments) in obligations)
        {
            var originated = instalments
                .SelectMany(i => i.Amounts)
                .GroupBy(c => c.Key)
                .Select(g => new Component(g.Key, Money.Sum(Currency.KES, g.Select(c => c.Amount))))
                .ToList();
            var plan = ScheduleBuilder.Build(1, PlanSpec.Custom(instalments), originated, Currency.KES, GracePeriod.None, BusinessCalendars.None, s_epoch);
            var id = new ObligationId(Guid.CreateVersion7());
            labels[id] = label;
            built.Add(new Obligation(id, accountId, $"loan-{label}", $"LN-{label}", Currency.KES, ObligationStatus.Active,
                [.. originated], [plan], null, s_epoch.AddMinutes(opened)));
        }

        var lot = FundsLot.Received(paymentId, pay!, null, s_epoch);
        var account = new CustomerAccount(s_tenant, accountId, "C-1", 1, s_epoch, built.ToImmutable(), [lot]);
        var eligibleIds = eligible.Count == 0
            ? labels.Keys.ToList()
            : [.. labels.Where(l => eligible.Contains(l.Value)).Select(l => l.Key)];

        return new Spec(account, paymentId, eligibleIds, strategy!, policy, asOf!.Value, labels, expected, unapplied!);
    }

    private static ComponentKey ParseComponent(string text)
    {
        var parts = text.Split('.', 2);
        return new ComponentKey(Enum.Parse<ComponentType>(parts[0], ignoreCase: true), parts.Length == 2 ? parts[1] : null);
    }

    private static string ComponentText(ComponentKey key) =>
        key.ChargeCode.Length == 0 ? key.Type.ToString().ToLowerInvariant() : $"{key.Type.ToString().ToLowerInvariant()}.{key.ChargeCode}";

    private static Money Kes(string amount) => Money.Kes(decimal.Parse(amount, CultureInfo.InvariantCulture));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InstalmentFlow.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed record Spec(
        CustomerAccount Account,
        PaymentId Lot,
        IReadOnlyCollection<ObligationId> Eligible,
        IAllocationStrategy Strategy,
        TenantPolicy Policy,
        DateOnly AsOf,
        IReadOnlyDictionary<ObligationId, string> Labels,
        IReadOnlyList<string> Expected,
        Money ExpectedUnapplied);
}
