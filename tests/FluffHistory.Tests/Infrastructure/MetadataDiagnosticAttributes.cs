using Xunit;

namespace FluffHistory.Tests.Infrastructure;

// Explicit opt-in also protects a plain `dotnet test`, outside the CI filter.
public sealed class MetadataDiagnosticFactAttribute : FactAttribute
{
    public MetadataDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLUFF_HISTORY_DIAGNOSTICS") != "1")
            Skip = "Opt-in metadata diagnostic: set FLUFF_HISTORY_DIAGNOSTICS=1 and select the desired fixture.";
    }
}

public sealed class MetadataDiagnosticTheoryAttribute : TheoryAttribute
{
    public MetadataDiagnosticTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLUFF_HISTORY_DIAGNOSTICS") != "1")
            Skip = "Opt-in metadata diagnostic: set FLUFF_HISTORY_DIAGNOSTICS=1 and select the desired fixture.";
    }
}
