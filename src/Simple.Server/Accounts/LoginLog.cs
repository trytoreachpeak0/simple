namespace Simple.Server.Accounts;

/// <summary>
/// Logging in and out goes to the program log (ADR 0034), with the computer
/// it came from, not to the event record (ADR 0045). Never the password.
/// </summary>
public static partial class LoginLog
{
    public const string Category = "Simple.Server.Accounts.Login";

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} logged in through {Entry} from {RemoteAddress}")]
    public static partial void LoggedIn(ILogger logger, string name, string entry, string? remoteAddress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed login as {Name} through {Entry} from {RemoteAddress}: wrong account name or password")]
    public static partial void Failed(ILogger logger, string? name, string? entry, string? remoteAddress);

    [LoggerMessage(Level = LogLevel.Information, Message = "The watch account {Name} tried to log in through {Entry} from {RemoteAddress} while on-duty watching is paused")]
    public static partial void RefusedWhilePaused(ILogger logger, string name, string entry, string? remoteAddress);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} logged out ({Entry}) from {RemoteAddress}")]
    public static partial void LoggedOut(ILogger logger, string? name, string? entry, string? remoteAddress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Too many password attempts from {RemoteAddress}; refused until its limit frees up")]
    public static partial void TooManyAttempts(ILogger logger, string? remoteAddress);
}
