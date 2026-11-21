using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace InstalmentFlow.Payments.Mpesa;

/// <summary>A C2B validation request: M-Pesa asks whether to accept a payment before it completes.</summary>
/// <param name="TransactionId">The receipt the payment will have.</param>
/// <param name="ShortCode">The shortcode paid.</param>
/// <param name="Amount">The amount, in whole shillings.</param>
/// <param name="AccountReference">The account number the customer typed.</param>
/// <param name="Payer">The payer, as M-Pesa shows it (masked or hashed).</param>
public sealed record MpesaValidationRequest(string TransactionId, string ShortCode, Money Amount, string? AccountReference, string? Payer);

/// <summary>Why a C2B payment is refused at validation (Daraja result codes).</summary>
public enum MpesaRejection
{
    /// <summary><c>C2B00011</c>: invalid MSISDN.</summary>
    InvalidMsisdn = 11,

    /// <summary><c>C2B00012</c>: invalid account number.</summary>
    InvalidAccountNumber = 12,

    /// <summary><c>C2B00013</c>: invalid amount.</summary>
    InvalidAmount = 13,
}

/// <summary>
/// C2B validation (doc 03 §6): synchronous, read-only, answered within about 8 seconds, and never stored in the inbox.
/// Accepting is the default, because a recorded unidentified payment is better than refused money (ADR-002).
/// </summary>
public static class MpesaValidation
{
    /// <summary>Reads a validation request.</summary>
    /// <exception cref="FormatException">The body is not a valid validation request.</exception>
    public static MpesaValidationRequest Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var receipt = MpesaJson.Text(root, "TransID");
            var shortCode = MpesaJson.Text(root, "BusinessShortCode");
            var amount = MpesaJson.Decimal(MpesaJson.Text(root, "TransAmount"));
            if (string.IsNullOrWhiteSpace(receipt) || string.IsNullOrWhiteSpace(shortCode) || amount is not > 0)
            {
                throw new FormatException("The validation request lacks TransID, BusinessShortCode or a positive TransAmount.");
            }

            var reference = MpesaJson.Text(root, "BillRefNumber")?.Trim();
            return new MpesaValidationRequest(receipt.Trim(), shortCode.Trim(), new Money(amount.Value, Currency.KES), string.IsNullOrEmpty(reference) ? null : reference, MpesaJson.Text(root, "MSISDN"));
        }
        catch (JsonException ex)
        {
            throw new FormatException("The validation request is not JSON.", ex);
        }
    }

    /// <summary>The reply that accepts the payment.</summary>
    public static byte[] Accept() => Reply("0", "Accepted");

    /// <summary>The reply that refuses the payment.</summary>
    public static byte[] Reject(MpesaRejection reason) => Reply($"C2B000{(int)reason}", "Rejected");

    /// <summary>The reply to a C2B confirmation, sent once the inbox has stored it.</summary>
    public static byte[] ConfirmationReceived() => Reply("0", "Success");

    private static byte[] Reply(string code, string description) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { ["ResultCode"] = code, ["ResultDesc"] = description }));
}

/// <summary>The Daraja security credential: the initiator password encrypted with Safaricom's public certificate.</summary>
public static class MpesaSecurityCredential
{
    /// <summary>Encrypts <paramref name="initiatorPassword"/> (RSA, PKCS #1 v1.5) with the certificate in <paramref name="certificatePem"/>, as base64.</summary>
    public static string Encrypt(string initiatorPassword, string certificatePem)
    {
        ArgumentException.ThrowIfNullOrEmpty(initiatorPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePem);
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        using var rsa = certificate.GetRSAPublicKey() ?? throw new InvalidOperationException("The certificate has no RSA public key.");
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(initiatorPassword), RSAEncryptionPadding.Pkcs1));
    }
}

/// <summary>Statement formats for M-Pesa exports.</summary>
public static class MpesaStatements
{
    /// <summary>
    /// The organisation portal's statement export, in Nairobi time, with its preamble skipped by finding the header row.
    /// The column names are not yet verified against a real export (docs/integrations/mpesa.md); pass
    /// <paramref name="valueAtFormat"/> if the times differ.
    /// </summary>
    public static CsvStatementFormat OrgPortal(string valueAtFormat = "yyyy-MM-dd HH:mm:ss") => new()
    {
        Currency = Currency.KES,
        TransactionId = "Receipt No.",
        ValueAt = "Completion Time",
        ValueAtFormat = valueAtFormat,
        PaidIn = "Paid In",
        Withdrawn = "Withdrawn",
        LinkedTransactionId = "Linked Transaction ID",
        AccountReference = "A/C No.",
        Payer = "Other Party Info",
        Status = "Transaction Status",
        TimeZone = "Africa/Nairobi",
        LocateHeader = true,
    };
}
