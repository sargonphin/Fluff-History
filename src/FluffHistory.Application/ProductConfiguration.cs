using System.Collections.Immutable;
using FluffHistory.Core;

namespace FluffHistory.Application;

public sealed record BackupFrequency(string Label, int Minutes);
public sealed record SavedVersionPolicy(string Label, string Name, RetentionPolicy Policy, string Notice)
{ public override string ToString() => Label; }
public sealed record TimelineCleanupChoice(string Label, HistoryCleanupMode Mode)
{ public override string ToString() => Label; }

/// <summary>The closed product menus; generic Engine/scheduler primitives remain independent.</summary>
public static class ProductConfiguration
{
    public const string DefaultRetentionPolicyName = "sixMonths";
    public static ImmutableArray<BackupFrequency> Frequencies { get; } =
    [new("Every 10 minutes", 10), new("Every 30 minutes", 30), new("Every hour", 60), new("Every 3 hours", 180),
     new("Every 6 hours", 360), new("Every 12 hours", 720), new("Every day", 1440)];
    private const string AgeNotice = "Versions older than this age can be removed after a successful backup. Current versions remain protected.";
    public static ImmutableArray<SavedVersionPolicy> RetentionPolicies { get; } =
    [new("Until space saturation", "untilSaturation", RetentionPolicy.UntilSaturation,
        "Versions are kept until the backup volume reaches its capacity safety threshold. When space is needed for a backup, the oldest eligible versions are removed automatically. Current versions remain protected."),
     new("1 month", "oneMonth", RetentionPolicy.OneMonth, AgeNotice), new("3 months", "threeMonths", RetentionPolicy.ThreeMonths, AgeNotice),
     new("6 months", "sixMonths", RetentionPolicy.SixMonths, AgeNotice), new("9 months", "nineMonths", RetentionPolicy.NineMonths, AgeNotice),
     new("1 year", "oneYear", RetentionPolicy.OneYear, AgeNotice), new("2 years", "twoYears", RetentionPolicy.TwoYears, AgeNotice),
     new("Forever", "forever", RetentionPolicy.Forever,
        "The \"Forever\" retention policy never removes Timeline history automatically. Versions will keep being added until the backup volume can no longer safely accept another backup. You will then need to free space or clean up the Timeline manually.")];
    public static ImmutableArray<TimelineCleanupChoice> CleanupChoices { get; } =
    [new("Older than 1 month", HistoryCleanupMode.OlderThanOneMonth), new("Older than 3 months", HistoryCleanupMode.OlderThanThreeMonths),
     new("Older than 6 months", HistoryCleanupMode.OlderThanSixMonths), new("Older than 9 months", HistoryCleanupMode.OlderThanNineMonths),
     new("Older than 1 year", HistoryCleanupMode.OlderThanOneYear), new("Older than 2 years", HistoryCleanupMode.OlderThanTwoYears),
     new("Keep only the most recent version", HistoryCleanupMode.KeepOnlyMostRecent)];
    public static SavedVersionPolicy Retention(string name) => RetentionPolicies.FirstOrDefault(p => p.Name == name)
        ?? throw new InvalidDataException("Unsupported saved-version policy.");
}
