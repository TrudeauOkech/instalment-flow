namespace InstalmentFlow;

/// <summary>Raised when amounts in different currencies are combined or compared (<c>IF1001</c>).</summary>
public sealed class CurrencyMismatchException : InstalmentFlowException
{
    /// <summary>Initialises the exception for the two currencies involved.</summary>
    public CurrencyMismatchException(Currency expected, Currency actual)
        : base(ErrorCodes.CurrencyMismatch, $"Currency mismatch: expected {expected}, got {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The currency the operation required.</summary>
    public Currency Expected { get; }

    /// <summary>The currency that was supplied.</summary>
    public Currency Actual { get; }
}

/// <summary>Raised when an amount is not valid for its currency, for example KES 1.005 (<c>IF1003</c>).</summary>
public sealed class InvalidAmountException : InstalmentFlowException
{
    /// <summary>Initialises the exception with a description of the problem.</summary>
    public InvalidAmountException(string message)
        : base(ErrorCodes.InvalidAmount, message)
    {
    }
}

/// <summary>Raised when a currency code is not recognised (<c>IF1004</c>).</summary>
public sealed class UnknownCurrencyException : InstalmentFlowException
{
    /// <summary>Initialises the exception for the unrecognised code.</summary>
    public UnknownCurrencyException(string code)
        : base(ErrorCodes.UnknownCurrency, $"Unknown currency code '{code}'.")
    {
        Code = code;
    }

    /// <summary>The code that was not recognised.</summary>
    public string Code { get; }
}
