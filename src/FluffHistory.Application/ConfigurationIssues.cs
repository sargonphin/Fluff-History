namespace FluffHistory.Application;

/// <summary>Presentation-neutral validation/admission reasons. Never configuration or repository data.</summary>
public enum ConfigurationIssueCode
{
    None, Checking, SettingsNeedReset, SavedConfigurationInvalid, WorkspaceUnavailable,
    ExclusionConflict, ExclusionLimit, EffectiveExclusionLimit, DuplicateFolder, NestedFolder,
    AbsolutePathRequired, OrdinaryPathRequired, CompleteShareRequired, AmbiguousPath, SourceRepositoryOverlap,
    FolderLimit, ConfirmWarning, ResolvedWarning, ResolvedOverlap, WorkingDataBlocked, FolderBlocked
}

public static class ConfigurationIssues
{
    private const string Key = "FluffHistory.ConfigurationIssue";
    /// <summary>Keep existing exception types/diagnostics while carrying a typed reason to presentation.</summary>
    public static InvalidDataException Invalid(ConfigurationIssueCode code, string diagnostic, Exception? inner = null)
    {
        var error = new InvalidDataException(diagnostic, inner); error.Data[Key] = code; return error;
    }
    public static ConfigurationIssueCode Code(Exception error) => error.Data[Key] is ConfigurationIssueCode code && Enum.IsDefined(code)
        ? code : ConfigurationIssueCode.SavedConfigurationInvalid;
}
