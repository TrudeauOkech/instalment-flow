using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace InstalmentFlow.Payments;

/// <summary>
/// The columns of a statement file. Column names are matched to the header row ignoring case and surrounding spaces.
/// A row with a positive <see cref="PaidIn"/> amount is a credit; a row with a <see cref="Withdrawn"/> amount and a
/// <see cref="LinkedTransactionId"/> is a reversal of that transaction; other rows (payouts, charges) are skipped.
/// </summary>
public sealed record CsvStatementFormat
{
    /// <summary>The currency of the amounts.</summary>
    public required Currency Currency { get; init; }

    /// <summary>The column with the provider's receipt.</summary>
    public required string TransactionId { get; init; }

    /// <summary>The column with when the money moved.</summary>
    public required string ValueAt { get; init; }

    /// <summary>The column with the amount received.</summary>
    public required string PaidIn { get; init; }

    /// <summary>The column with the amount paid out, for reversals.</summary>
    public string? Withdrawn { get; init; }

    /// <summary>The column naming the transaction a reversal reverses.</summary>
    public string? LinkedTransactionId { get; init; }

    /// <summary>The column with the payer's account reference.</summary>
    public string? AccountReference { get; init; }

    /// <summary>The column with the payer.</summary>
    public string? Payer { get; init; }

    /// <summary>The column with each row's status; with it, only rows whose status is in <see cref="CompletedStatuses"/> count.</summary>
    public string? Status { get; init; }

    /// <summary>The statuses of rows that moved money, compared ignoring case. Default <c>Completed</c>.</summary>
    public IReadOnlyList<string> CompletedStatuses { get; init; } = ["Completed"];

    /// <summary>
    /// The exact format of <see cref="ValueAt"/>, for example <c>yyyy-MM-dd HH:mm:ss</c>. Without an offset in the
    /// format, times are local to <see cref="TimeZone"/>.
    /// </summary>
    public string ValueAtFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";

    /// <summary>The IANA time zone of times without an offset, for example <c>Africa/Nairobi</c>. Default UTC.</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>The field separator. Default comma.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>Lines before the header row, for statements with a preamble. Default none.</summary>
    public int SkipLines { get; init; }

    /// <summary>Finds the header row as the first row with a <see cref="TransactionId"/> column, for preambles of varying length.</summary>
    public bool LocateHeader { get; init; }
}

/// <summary>
/// A statement read from a CSV file (RFC 4180: quoted fields, doubled quotes, line breaks inside quotes), for
/// reconciliation with an exported provider statement. The whole file is read when the source is created, so a
/// malformed file fails before any line is reconciled (<c>IF6005</c>).
/// </summary>
public sealed class CsvStatementSource : IStatementSource
{
    private readonly IReadOnlyList<ProviderStatementLine> _lines;

    private CsvStatementSource(string provider, IReadOnlyList<ProviderStatementLine> lines)
    {
        Provider = provider;
        _lines = lines;
    }

    /// <inheritdoc />
    public string Provider { get; }

    /// <summary>Every line read from the file.</summary>
    public IReadOnlyList<ProviderStatementLine> Lines => _lines;

    /// <summary>Reads a statement of <paramref name="providerAccount"/> at <paramref name="provider"/>.</summary>
    public static async Task<CsvStatementSource> ReadAsync(string provider, string providerAccount, TextReader reader, CsvStatementFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerAccount);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(format);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return new CsvStatementSource(provider, Parse(provider, providerAccount, text, format));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProviderStatementLine> ReadAsync(string providerAccount, DateTimeOffset windowStart, DateTimeOffset windowEnd, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var line in _lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.ProviderAccount == providerAccount && line.ValueAt >= windowStart && line.ValueAt < windowEnd)
            {
                yield return line;
            }
        }
    }

    private static List<ProviderStatementLine> Parse(string provider, string providerAccount, string text, CsvStatementFormat format)
    {
        var rows = Rows(text, format.Delimiter).Skip(format.SkipLines).ToList();
        if (format.LocateHeader)
        {
            var headerRow = rows.FindIndex(r => r.Fields.Any(f => string.Equals(f.Trim(), format.TransactionId.Trim(), StringComparison.OrdinalIgnoreCase)));
            rows = headerRow < 0 ? [] : rows[headerRow..];
        }
        if (rows.Count == 0)
        {
            throw Invalid(0, "The file has no header row.");
        }

        var header = rows[0].Fields;
        int Column(string name, bool required)
        {
            var index = header.FindIndex(h => string.Equals(h.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            return index < 0 && required ? throw Invalid(rows[0].Line, $"Column '{name}' is missing.") : index;
        }

        var transaction = Column(format.TransactionId, true);
        var valueAt = Column(format.ValueAt, true);
        var paidIn = Column(format.PaidIn, true);
        var withdrawn = format.Withdrawn is { } w ? Column(w, true) : -1;
        var linked = format.LinkedTransactionId is { } l ? Column(l, true) : -1;
        var reference = format.AccountReference is { } r ? Column(r, true) : -1;
        var payer = format.Payer is { } p ? Column(p, true) : -1;
        var status = format.Status is { } st ? Column(st, true) : -1;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(format.TimeZone);

        var lines = new List<ProviderStatementLine>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string Field(int index) => index >= 0 && index < row.Fields.Count ? row.Fields[index].Trim() : string.Empty;
            if (status >= 0 && !format.CompletedStatuses.Contains(Field(status), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            var id = Field(transaction);
            if (id.Length == 0)
            {
                throw Invalid(row.Line, "The transaction ID is empty.");
            }

            var at = Time(Field(valueAt), format, zone) ?? throw Invalid(row.Line, $"'{Field(valueAt)}' is not a time in the format '{format.ValueAtFormat}'.");
            var credit = Amount(Field(paidIn)) ?? throw Invalid(row.Line, $"'{Field(paidIn)}' is not an amount.");
            var debit = withdrawn >= 0 ? Amount(Field(withdrawn)) ?? throw Invalid(row.Line, $"'{Field(withdrawn)}' is not an amount.") : 0m;
            if (credit > 0)
            {
                lines.Add(new ProviderStatementLine
                {
                    Provider = provider, ProviderAccount = providerAccount, TransactionId = id, Kind = StatementLineKind.Credit,
                    Amount = new Money(credit, format.Currency), ValueAt = at, AccountReference = NullIfEmpty(Field(reference)), Payer = NullIfEmpty(Field(payer)),
                });
            }
            else if (debit > 0 && NullIfEmpty(Field(linked)) is { } reversed)
            {
                lines.Add(new ProviderStatementLine
                {
                    Provider = provider, ProviderAccount = providerAccount, TransactionId = id, Kind = StatementLineKind.Reversal,
                    Amount = new Money(debit, format.Currency), ValueAt = at, ReversedTransactionId = reversed, Payer = NullIfEmpty(Field(payer)),
                });
            }
        }

        return lines;
    }

    private static DateTimeOffset? Time(string value, CsvStatementFormat format, TimeZoneInfo zone)
    {
        if (!DateTimeOffset.TryParseExact(value, format.ValueAtFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return null;
        }

        if (format.ValueAtFormat.Contains('z', StringComparison.Ordinal) || format.ValueAtFormat.Contains('K', StringComparison.Ordinal))
        {
            return parsed;
        }

        // The digits are wall-clock time in the zone: take the offset in force at that local time.
        var guess = zone.GetUtcOffset(parsed);
        var offset = zone.GetUtcOffset(new DateTimeOffset(parsed.Ticks, TimeSpan.Zero) - guess);
        return new DateTimeOffset(parsed.Ticks, offset);
    }

    private static decimal? Amount(string value)
    {
        if (value.Length == 0)
        {
            return 0m;
        }

        return decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowParentheses, CultureInfo.InvariantCulture, out var amount) ? Math.Abs(amount) : null;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static RuleViolationException Invalid(int line, string message) =>
        new(PaymentsErrorCodes.InvalidStatement, line > 0 ? $"Statement line {line}: {message}" : message);

    private static IEnumerable<(int Line, List<string> Fields)> Rows(string text, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var start = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    line += c == '\n' ? 1 : 0;
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                yield return (start, fields);
                fields = [];
                line++;
                start = line;
            }
            else
            {
                field.Append(c);
            }
        }

        if (quoted)
        {
            throw Invalid(start, "A quoted field is not closed.");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (start, fields);
        }
    }
}
