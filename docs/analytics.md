# Analytics

**Analytics** in the dashboard sidebar (under Observe) shows where an organization's tokens and
money go, which models and tools are worth it, and what takes long. It has two views: **Tasks** and
**Workspaces**. Everything is per organization.

## Tasks

| Panel | Answers |
|---|---|
| **Spend, tokens, runs, average spend and tokens per run, median and p95 duration** | The headline figures, with the change against the previous period of the same length. |
| **Over time** | Spend, tokens, runs or average duration per hour (ranges up to two days) or per day, in your timezone. Click a bar to zoom into that hour or day; drag the handles under the chart to focus on part of a long range. |
| **What consumes the most** | Tokens or spend by agent role, with each role's share. |
| **Spend by source** | Dashboard/API, MCP, A2A, ACP or replay. Click a slice to filter by it. |
| **By model** | Spend, tokens, cost per call and response time (average and p95) for each model. Click one to filter by it. |
| **Tools** | Total time, average time, calls or failures per tool: which tools agents wait on and which fail. |
| **How long runs take** | Finished runs by duration. |
| **Runs to look at** | The most expensive and the slowest runs, linking to each run's agent graph. |

## Workspaces

| Panel | Answers |
|---|---|
| **Spend, tokens, average spend per day, model calls, triggers fired, approvals** | The headline figures, with the change against the previous period. Model calls show average and p95 response time; approvals how many were approved, rejected or expired. |
| **Over time** | Spend, tokens, model calls or response time per hour or day. |
| **By workspace** | Spend, tokens, model calls, triggers and approvals for each workspace. Click one to focus on it. |
| **What consumes the most** | Tokens or spend by agent role (coordinator, standing agents, workers). |
| **Tools** and **By model** | As for tasks. |

## Filters

A preset range (24 hours, 7, 30 or 90 days) or any from/to dates, and a model. For tasks also the
source, the status (completed, failed, running) and a search on the goal; for workspaces, one
workspace. Filters live in the page's URL, so a filtered view can be bookmarked or shared.

## Notes

- A task counts in the period it started in, with all of its agents' tokens and spend. Workspace
  activity counts when it happened.
- Filtering tasks by model keeps the runs that used it for at least one call (a run can switch
  models part-way, see [llm-settings.md](llm-settings.md)).
- Model calls (by model, workspace spend and response times) and tool times are recorded from this
  release on. Older tasks still show their spend; older workspace activity is in
  **Settings → Usage & billing**.
- Up to 20,000 runs and 100,000 model calls per query are included (the response says
  `truncated: true` beyond that).

## API

`GET /api/analytics?range=7d` (or `from=…&to=…`), with optional `scope` (`tasks` or
`workspaces`), `model` (a model id), `source`, `status` (`running`, `completed`, `failed`) and `q`
(goal search) for tasks, `workspace` for workspaces, and `tz_offset_minutes` (as JavaScript's
`getTimezoneOffset()`, for day boundaries). Any member of the organization can read it.
