namespace UtpStream;

/// <summary>
/// Errors surfaced by the µTP transport. <see cref="Code"/> mirrors libutp's
/// <c>UTP_ECONN*</c>/<c>UTP_ETIMEDOUT</c> error codes.
/// </summary>
public sealed class UtpException : IOException
{
    public UtpErrorCode Code { get; }

    public UtpException(UtpErrorCode code, string? message = null)
        : base(message ?? Describe(code))
    {
        Code = code;
    }

    private static string Describe(UtpErrorCode code) => code switch
    {
        UtpErrorCode.ConnectionRefused => "µTP connection refused.",
        UtpErrorCode.ConnectionReset => "µTP connection reset by peer.",
        UtpErrorCode.TimedOut => "µTP connection timed out.",
        _ => $"µTP error {(int)code}.",
    };
}

public enum UtpErrorCode
{
    ConnectionRefused = 0,
    ConnectionReset = 1,
    TimedOut = 2,
}
