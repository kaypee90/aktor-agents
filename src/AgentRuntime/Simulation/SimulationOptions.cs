using AgentRuntime.Contracts;

namespace AgentRuntime.Simulation;

/// <summary>
/// Rules of every simulated world. All of these are enforced by the world grain — residents are
/// told the costs, but the runtime is what actually charges energy and rejects actions.
/// </summary>
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    // ---- Population and time bounds (the cost bound: a world always ends) ----
    public int MaxInitialPopulation { get; set; } = 12;
    public int MaxPopulation { get; set; } = 20;
    public int DefaultTickIntervalSeconds { get; set; } = 15;
    public int MinTickIntervalSeconds { get; set; } = 5;
    public int DefaultMaxTicks { get; set; } = 40;
    public int MaxTicksCeiling { get; set; } = 500;
    public int DefaultMaxDurationMinutes { get; set; } = 20;
    public int MaxDurationMinutesCeiling { get; set; } = 240;
    public int MaxMessagesPerWorld { get; set; } = 5000;

    // ---- Energy economy ----
    public int StartingEnergy { get; set; } = 100;
    public int MaxEnergy { get; set; } = 100;
    public int EnergyRegenPerTick { get; set; } = 4;
    public int NewbornEnergy { get; set; } = 50;
    public EnergyCosts Costs { get; set; } = new();

    // ---- Vote-based removal ----
    /// <summary>A proposal stays open this many ticks unless the vote is decided sooner.</summary>
    public int VoteWindowTicks { get; set; } = 3;
    /// <summary>Removal needs at least this many eligible voters, so two residents alone can't
    /// remove a third on a whim, and a lone proposer can never remove anyone by themselves.</summary>
    public int MinEligibleVoters { get; set; } = 3;

    // ---- Resident agent turn shape ----
    /// <summary>LLM iterations one resident may take per wake-up before the runtime parks it.</summary>
    public int ResidentMaxIterationsPerTurn { get; set; } = 6;
    /// <summary>How many recent transcript entries a resident's LLM call sees. Without a window,
    /// every tick would resend the resident's whole life story and costs would grow quadratically.</summary>
    public int ResidentTranscriptWindow { get; set; } = 30;
    public double ResidentTemperature { get; set; } = 0.8;
    public int MaxNotesPerResident { get; set; } = 8;
    public int MaxBoardPosts { get; set; } = 40;
    public int MaxActivityLog { get; set; } = 600;
    public int MaxTextLength { get; set; } = 600;

    /// <summary>Per-resident safety budget. The world's tick/time limits are the main cost bound;
    /// this stops any single resident running away (e.g. an endless direct-message exchange).</summary>
    public int ResidentMaxTokens { get; set; } = 400_000;
    public int ResidentMaxToolCalls { get; set; } = 800;
    public decimal ResidentMaxCostUsd { get; set; } = 1.50m;

    public ResourceBudget ResidentBudget(int maxDurationMinutes) => new()
    {
        MaxTokens = ResidentMaxTokens,
        MaxToolCalls = ResidentMaxToolCalls,
        MaxCostUsd = ResidentMaxCostUsd,
        MaxChildren = 3,
        MaxDurationSeconds = maxDurationMinutes * 60
    };
}

public sealed class EnergyCosts
{
    public int Say { get; set; } = 1;
    public int TalkTo { get; set; } = 2;
    public int Move { get; set; } = 3;
    public int Post { get; set; } = 2;
    public int ProposeRemoval { get; set; } = 10;
    public int Vote { get; set; } = 0;
    public int BringNewAgent { get; set; } = 40;

    public string Describe() =>
        $"say {Say}, talk_to {TalkTo}, move_to {Move}, post_to_board {Post}, propose_removal {ProposeRemoval}, " +
        $"vote {Vote}, bring_new_agent {BringNewAgent}, give_energy = the amount given; " +
        "look_around, note_to_self, end_turn and leave_world are free";
}
