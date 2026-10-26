namespace FluffHistory.Storage;

/// <summary>Only password-slot authentication failed. Does not classify authenticated state/root damage.
/// A wrong password and a tampered authenticated slot cannot be distinguished cryptographically.</summary>
public sealed class RecoveryPasswordAuthenticationException(Exception cause)
    : IOException("Recovery password authentication failed.", cause);
