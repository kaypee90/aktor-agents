# Evals

Agent teams form on their own, so "did this change make things better or worse?" needs measuring
rather than guessing. `aktor-eval` (`src/AgentRuntime.Evals`) runs goals N times through the real
runtime, in process with in-memory storage and no database, and reports per scenario and variant:

| Metric | |
|---|---|
| Team size and depth | Agents in the tree (mean, max) and how deep it went |
| Cost and duration | Tokens and dollars (mean), seconds (p50, p95) |
| Completion rate | Runs whose root reported `completed`, rather than partial, failed or timed out |
| Output quality | 0–5, judged per run (see below) |
| Replay fidelity | For `replay` variants: share of replays whose decisions matched the recording step for step |

**Variants** are what you compare:
- a different model: `"settings": {"Llm:Model": "claude-haiku-4-5"}`;
- a different prompt: `"settings": {"Prompts:ExtraInstructions": "…"}`;
- any runtime setting;
- `"mode": "replay"`: run once live, then replay the rest through the step journal
  ([replay.md](replay.md)). This checks that replay reproduces runs, and gives cheap, deterministic
  re-runs.

Every row shows the model and a **prompt version**: a hash of the stable part of the root agent's
system prompt. Rows are therefore only ever compared like with like, and a prompt change shows up
as a new version.

**Quality.**
- With a real model, an LLM judge grades each output through a structured rubric tool
  (relevance, completeness, specificity, actionability, from 0 to 5) and the score is the mean.
- With the Mock provider (or `"judge": "heuristic"`), a deterministic check scores one point each
  for: a summary, being on topic, specialists' findings, a written deliverable, and coverage of the
  scenario's `expected_topics`.

## Running it

```bash
dotnet build AktorAgents.slnx
# The demo scenarios (CLAUDE.md §34 and §35) on the Mock provider, compared with the baseline:
dotnet run --project src/AgentRuntime.Evals -- --spec evals/demo-scenarios.json --out evals/reports/latest \
    --baseline evals/baseline/report.json --set Llm:Provider=Mock

# A real model (the key comes from the environment, never the spec):
Llm__ApiKey=… dotnet run --project src/AgentRuntime.Evals -- --spec evals/demo-scenarios.json \
    --out evals/reports/sonnet --set Llm:Provider=Anthropic --set Llm:Model=claude-sonnet-5 --runs 5
```

- Runtime defaults (budgets, limits) come from `src/AgentRuntime.Api/appsettings.json`, so evals
  measure what users get. Use `--config` for another file; connection strings and keys in it are
  ignored.
- Results go to `report.json` and `report.md`. The Markdown includes each variant compared with the
  first one.
- With `--baseline`, the exit code is 1 when a scenario/variant regressed:
  - completion fell by more than 10 points;
  - quality fell by more than 0.5;
  - mean cost grew by more than 25%;
  - mean team size grew by more than 50%;
  - replay fidelity fell at all.

## Baseline and CI

`evals/baseline/` holds the committed baseline for both demo scenarios on the Mock provider.
- CI (`.github/workflows/ci.yml`) re-runs it on every push and pull request, and uploads the report.
- `EvalHarnessTests` runs the same check in the integration test suite.
- After an intended change (for example a new prompt section that makes every call bigger),
  regenerate the baseline and commit it with the change:

```bash
dotnet run --project src/AgentRuntime.Evals -- --spec evals/demo-scenarios.json --out evals/baseline --set Llm:Provider=Mock
```

The Mock provider ignores prompts. On it, the `lean-teams` variant only shows that prompt versions
are tracked; with a real model it measures whether the instruction actually shrinks teams.
