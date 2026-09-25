namespace AgentRuntime.Durability;

/// <summary>
/// Crash-recovery settings (see docs/durability.md). Recovery is driven by Orleans reminders,
/// which are stored durably and fire on whichever silo is alive, so work interrupted by a crash
/// resumes within about one period of a silo being available again.
/// </summary>
public sealed class DurabilityOptions
{
    public const string SectionName = "Durability";

    /// <summary>How often a durable reminder checks for interrupted work. Orleans enforces a
    /// minimum (ReminderOptions.MinimumReminderPeriod, one minute by default).</summary>
    public TimeSpan RecoveryReminderPeriod { get; set; } = TimeSpan.FromMinutes(1);
}
