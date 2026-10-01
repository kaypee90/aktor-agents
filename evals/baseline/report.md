# Eval report

Generated 2026-10-01 19:20:55Z; quality judged by `heuristic` (0–5).

| Scenario | Variant | Model | Prompt | Runs | Completed | Team (mean, max) | Depth (max) | Tokens (mean) | Cost $ (mean) | Duration s (p50 / p95) | Quality (mean) | Replay fidelity |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| feasibility-research | default | Mock:claude-sonnet-5 | `f068fb0255ac` | 3 | 100 % | 4.3, 5 | 2 | 16,551 | 0.0564 | 0.1 / 2.1 | 4.53 | – |
| add-authentication | default | Mock:claude-sonnet-5 | `f068fb0255ac` | 3 | 100 % | 5, 5 | 2 | 17,522 | 0.0598 | 0.1 / 0.2 | 4.00 | – |
| feasibility-research | lean-teams | Mock:claude-sonnet-5 | `d59135cf5805` | 3 | 100 % | 4.3, 5 | 2 | 16,838 | 0.0572 | 0.1 / 0.3 | 4.60 | – |
| add-authentication | lean-teams | Mock:claude-sonnet-5 | `d59135cf5805` | 3 | 100 % | 5, 5 | 2 | 17,827 | 0.0607 | 0.1 / 0.2 | 4.00 | – |
| feasibility-research | replay | Mock:claude-sonnet-5 | `f068fb0255ac` | 3 | 100 % | 4, 4 | 2 | 15,967 | 0.0544 | 0.1 / 0.2 | 4.60 | 100 % |
| add-authentication | replay | Mock:claude-sonnet-5 | `f068fb0255ac` | 3 | 100 % | 5, 5 | 2 | 17,522 | 0.0598 | 0.1 / 0.1 | 4.00 | 100 % |

## feasibility-research: compared with `default`

| Variant | Completion | Team size | Cost | Quality |
|---|---|---|---|---|
| lean-teams | +0 | +0 % | +1 % | +0.07 |
| replay | +0 | -8 % | -4 % | +0.07 |

## add-authentication: compared with `default`

| Variant | Completion | Team size | Cost | Quality |
|---|---|---|---|---|
| lean-teams | +0 | +0 % | +2 % | +0 |
| replay | +0 | +0 % | +0 % | +0 |
