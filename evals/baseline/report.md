# Eval report

Generated 2026-10-08 18:31:05Z; quality judged by `heuristic` (0–5).

| Scenario | Variant | Model | Prompt | Runs | Completed | Team (mean, max) | Depth (max) | Tokens (mean) | Cost $ (mean) | Duration s (p50 / p95) | Quality (mean) | Replay fidelity |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| feasibility-research | default | Mock:claude-sonnet-5 | `ca1f9d321e25` | 3 | 100 % | 4.3, 5 | 2 | 20,937 | 0.0695 | 0.0 / 0.5 | 4.60 | – |
| add-authentication | default | Mock:claude-sonnet-5 | `ca1f9d321e25` | 3 | 100 % | 5, 5 | 2 | 22,188 | 0.0738 | 0.0 / 0.0 | 4.00 | – |
| feasibility-research | lean-teams | Mock:claude-sonnet-5 | `0e252bc31bcc` | 3 | 100 % | 4.3, 5 | 2 | 21,220 | 0.0704 | 0.0 / 0.1 | 4.60 | – |
| add-authentication | lean-teams | Mock:claude-sonnet-5 | `0e252bc31bcc` | 3 | 100 % | 5, 5 | 2 | 22,496 | 0.0747 | 0.0 / 0.1 | 4.00 | – |
| feasibility-research | replay | Mock:claude-sonnet-5 | `ca1f9d321e25` | 3 | 100 % | 4, 4 | 2 | 20,213 | 0.0671 | 0.0 / 0.1 | 4.60 | 100 % |
| add-authentication | replay | Mock:claude-sonnet-5 | `ca1f9d321e25` | 3 | 100 % | 5, 5 | 2 | 22,188 | 0.0738 | 0.0 / 0.0 | 4.00 | 100 % |

## feasibility-research: compared with `default`

| Variant | Completion | Team size | Cost | Quality |
|---|---|---|---|---|
| lean-teams | +0 | +0 % | +1 % | +0 |
| replay | +0 | -8 % | -3 % | +0 |

## add-authentication: compared with `default`

| Variant | Completion | Team size | Cost | Quality |
|---|---|---|---|---|
| lean-teams | +0 | +0 % | +1 % | +0 |
| replay | +0 | +0 % | +0 % | +0 |
