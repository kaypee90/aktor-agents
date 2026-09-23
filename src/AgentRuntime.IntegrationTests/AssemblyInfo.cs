using Xunit;

// The scripted LLM provider used by these tests is configured via a static, process-wide current
// script (see ScriptedLlmProviderRegistry) rather than per-test DI, so test classes must not run
// concurrently with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
