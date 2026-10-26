namespace FluffHistory.Core;

/// <summary>Closed product menu. Numeric values are not a persistence contract.</summary>
public enum RetentionPolicy { UntilSaturation, OneMonth, ThreeMonths, SixMonths, NineMonths, OneYear, TwoYears, Forever }

public sealed record RetentionEvaluation
{
    public RetentionPolicy Policy { get; }
    public DateTimeOffset EvaluationUtc { get; }
    public DateTimeOffset? CutoffUtc { get; }
    public RetentionEvaluation(RetentionPolicy policy, DateTimeOffset evaluationUtc)
    {
        ModelValidation.RequireUtc(evaluationUtc, nameof(evaluationUtc));
        var days = policy switch
        {
            RetentionPolicy.OneMonth => 30, RetentionPolicy.ThreeMonths => 90,
            RetentionPolicy.SixMonths => 180, RetentionPolicy.NineMonths => 270,
            RetentionPolicy.OneYear => 365, RetentionPolicy.TwoYears => 730,
            RetentionPolicy.Forever => 0,
            RetentionPolicy.UntilSaturation => throw new NotSupportedException("UntilSaturation requires the separate capacity-retention workflow."),
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
        Policy = policy; EvaluationUtc = evaluationUtc;
        if (days != 0) CutoffUtc = new DateTimeOffset(checked(evaluationUtc.Ticks - checked(days * TimeSpan.TicksPerDay)), TimeSpan.Zero);
    }
}

/// <summary>Validated surviving version projection; contains no payload or restore data.</summary>
public sealed record RetentionVersion(VersionIdentity Identity, DateTimeOffset StoredUtc, long Length, bool IsCurrent);
