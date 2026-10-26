namespace FluffHistory.Storage;

/// <summary>Only local key-slot unwrapping failed. Repository/bootstrap read or authenticated state failures
/// are not reclassified as recovery. Caller may offer explicit password recovery and enrollment.</summary>
public sealed class WindowsUserProtectorUnavailableException(Exception cause)
    : IOException("Local Windows protector unavailable. Explicit password recovery is required.", cause);
