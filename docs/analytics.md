# Analytics

**Analytics** in the dashboard sidebar (under Observe) shows where an organization's tokens and
money go, which models and tools are worth it, and what takes long. It has two views: **Tasks** and
**Workspaces**. Everything is per organization.

## Tasks

| Panel | Answers |
|---|---|
| **Spend, tokens, runs, average spend and tokens per run, median and p95 duration** | The headline figures, with the change against the previous period of the same length. |
| **Over time** | Spend, tokens, runs or average duration per hour (ranges up to two days) or per day, in your timezone. Click a bar to zoom into that hour or day; drag the handles under the chart to focus on part of a long range. |
| **Usage** | Runs, model calls, active days, spend and average spend per active day, average and median tokens per run, and tokens by kind: input, output, cache reads and cache writes, with the share of input served from the prompt cache. |
| **What consumes the most** | Tokens or spend by agent role, with each role's share. |
| **Spend by source** | Dashboard/API, MCP, A2A, ACP or replay. Click a slice to filter by it. |
| **By user** | Who started the runs: each member (or API key, for runs started with one), with runs, spend and its share, tokens, average spend per run, failures and their last run. Click one to filter by them. |
| **By model** | Calls, input, output, cache-read and cache-write tokens, spend, cost per call and response time (average and p95) for each model. Click one to filter by it. |
| **Tools** | Total time, average time, calls (with each tool's share of all calls) or failures per tool: which tools agents wait on and which fail. |
| **How long runs take** | Finished runs by duration. |
| **Runs to look at** | The most expensive and the slowest runs, with who started each, linking to each run's agent graph. |

## Workspaces

| Panel | Answers |
|---|---|
| **Spend, tokens, average spend per day, model calls, triggers fired, approvals** | The headline figures, with the change against the previous period. Model calls show average and p95 response time; approvals how many were approved, rejected or expired. |
| **Over time** | Spend, tokens, runs, model calls or response time per hour or day. |
| **Pipeline runs, median run time** | How many runs started (done, failed, running) against the previous period, how long they took (median and p95) and what a run costs on average. |
| **By workspace** | Spend, tokens, runs (and failures), model calls, triggers and approvals for each workspace. Click one to focus on it. |
| **By user** | Who started the runs: members, API keys, or triggers, with runs, spend and its share, failures and their last run. |
| **Runs to look at** | The most expensive and the slowest runs, with their workspace and who started them, linking to each run's agent graph. |
| **What consumes the most** | Tokens or spend by agent role: pipeline stages and their helpers. |
| **Usage**, **Tools** and **By model** | As for tasks. |

## Filters

A preset range (24 hours, 7, 30 or 90 days) or any from/to dates, and a model. For tasks also the
source, the status (completed, failed, running), who started it and a search on the goal; for workspaces, one
workspace. Filters live in the page's URL, so a filtered view can be bookmarked or shared.

## Notes

- A task counts in the period it started in, with all of its agents' tokens and spend. Workspace
  activity counts when it happened.
- Filtering tasks by model keeps the runs that used it for at least one call (a run can switch
  models part-way, see [llm-settings.md](llm-settings.md)).
- Model calls (by model, workspace spend and response times) and tool times are recorded from this
  release on. Older tasks still show their spend; older workspace activity is in
  **Settings → Usage & billing**.
- A run belongs to whoever started it, with all of its agents' spend: the signed-in member for the
  dashboard, or the API key for the API, MCP, A2A and ACP. A replay belongs to whoever replayed
  it. Runs from before this was recorded show as **Not recorded**; with sign-in off, everything is
  **Local**. A member or key deleted since keeps its runs, under a short id.
- A workspace pipeline's runs are reported in the **Workspaces** view, not under Tasks (though
  each run opens as a task). There, runs are attributed to whoever started them, or **Triggers
  (automatic runs)** for runs a schedule, webhook or watch started.
- Input tokens are the uncached ones: cache reads and cache writes are counted apart, so the four
  kinds add up to every token sent and received. Cache writes are recorded from this release on.
- Up to 20,000 runs and 100,000 model calls per query are included (the response says
  `truncated: true` beyond that).

## API

`GET /api/analytics?range=7d` (or `from=…&to=…`), with optional `scope` (`tasks` or
`workspaces`), `model` (a model id), `source`, `status` (`running`, `completed`, `failed`), `user` (a
`by_user[].user` value: a user id, `key:<id>`, or `unknown`) and `q` (goal search) for tasks, `workspace` for workspaces, and `tz_offset_minutes` (as JavaScript's
`getTimezoneOffset()`, for day boundaries). Any member of the organization can read it.
