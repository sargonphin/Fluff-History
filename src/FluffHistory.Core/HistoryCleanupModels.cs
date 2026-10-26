namespace FluffHistory.Core;

/// <summary>One-shot manual commands, independent of the configured automatic RetentionPolicy.</summary>
public enum HistoryCleanupMode
{
    OlderThanOneMonth, OlderThanThreeMonths, OlderThanSixMonths, OlderThanNineMonths,
    OlderThanOneYear, OlderThanTwoYears, KeepOnlyMostRecent
}

public sealed record HistoryCleanupEvaluation
{
    public HistoryCleanupMode Mode { get; }
    public DateTimeOffset? EvaluationUtc { get; }
    public DateTimeOffset? CutoffUtc { get; }

    public HistoryCleanupEvaluation(HistoryCleanupMode mode)
    {
        if (mode != HistoryCleanupMode.KeepOnlyMostRecent)
            throw new ArgumentException("Age cleanup requires an explicit UTC evaluation instant.", nameof(mode));
        Mode = mode;
    }

    public HistoryCleanupEvaluation(HistoryCleanupMode mode, DateTimeOffset evaluationUtc)
    {
        var policy = mode switch
        {
            HistoryCleanupMode.OlderThanOneMonth => RetentionPolicy.OneMonth,
            HistoryCleanupMode.OlderThanThreeMonths => RetentionPolicy.ThreeMonths,
            HistoryCleanupMode.OlderThanSixMonths => RetentionPolicy.SixMonths,
            HistoryCleanupMode.OlderThanNineMonths => RetentionPolicy.NineMonths,
            HistoryCleanupMode.OlderThanOneYear => RetentionPolicy.OneYear,
            HistoryCleanupMode.OlderThanTwoYears => RetentionPolicy.TwoYears,
            HistoryCleanupMode.KeepOnlyMostRecent => throw new ArgumentException("KeepOnlyMostRecent has no evaluation time.", nameof(mode)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        // Share the established UTC validation, elapsed durations and checked tick calculation.
        var age = new RetentionEvaluation(policy, evaluationUtc);
        Mode = mode; EvaluationUtc = age.EvaluationUtc; CutoffUtc = age.CutoffUtc;
    }
}
