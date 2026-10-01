using AgentRuntime.Memory;

namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>
/// A deterministic stand-in for a real embedding model: each word maps to a "concept" axis, so
/// texts about the same thing in different words ("renters move out" / "why do tenants leave")
/// get similar vectors. Enough to prove the semantic-recall pipeline (pgvector storage, cosine
/// ranking, model scoping) without a model download.
/// </summary>
public sealed class ConceptEmbeddingProvider : IEmbeddingProvider
{
    private static readonly string[][] Concepts =
    [
        ["tenant", "tenants", "renter", "renters", "resident", "residents", "occupant", "occupants"],
        ["leave", "leaving", "move", "moving", "churn", "quit", "vacate", "vacating", "departure"],
        ["maintenance", "repair", "repairs", "fix", "broken", "plumbing"],
        ["slow", "weeks", "delay", "delays", "late", "waiting"],
        ["billing", "payment", "payments", "stripe", "invoice", "invoices"],
        ["webhook", "webhooks", "signature", "hmac", "http"],
        ["price", "pricing", "cost", "subscription", "plan"],
        ["competitor", "competitors", "rival", "rivals", "appfolio", "buildium"]
    ];

    public bool IsConfigured => true;
    public string Model => "test:concepts-v1";
    public int Calls;

    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        var vector = new float[Concepts.Length + 1];
        foreach (var word in text.ToLowerInvariant().Split([' ', '\n', ',', '.', '?', '!', ':', ';', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var axis = Array.FindIndex(Concepts, c => c.Contains(word));
            vector[axis < 0 ? Concepts.Length : axis] += axis < 0 ? 0.05f : 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return Task.FromResult<float[]?>(norm == 0 ? null : vector.Select(v => v / norm).ToArray());
    }
}
