# Analytics

**Analytics** in the dashboard sidebar (under Observe) shows where an organization's tokens and
money go, and what takes long. Everything is per organization.

## What it shows

| Panel | Answers |
|---|---|
| **Spend, tokens, runs, average spend and tokens per run, median and p95 duration** | The headline figures, with the change against the previous period of the same length. |
| **Over time** | Spend, tokens, runs or average duration per hour (ranges up to two days) or per day, in your timezone. Click a bar to zoom into that hour or day; drag the handles under the chart to focus on part of a long range. |
| **What consumes the most** | Tokens or spend by agent role across every run in range, with each role's share of all tokens. |
| **Spend by source** | Dashboard/API, MCP, A2A, ACP or replay. Click a slice to filter by it. |
| **Tools** | Total time, average time, calls or failures per tool: which tools agents wait on and which fail. |
| **How long runs take** | Finished runs by duration. |
| **Runs to look at** | The most expensive and the slowest runs, linking to each run's agent graph. |

## Filters

A preset range (24 hours, 7, 30 or 90 days) or any from/to dates, the source, the status
(completed, failed, running) and a search on the goal. Filters live in the page's URL, so a
filtered view can be bookmarked or shared with your team.

## Notes

- A run counts in the period it started in, with all of its agents' tokens and spend.
- Workspace activity outside tasks (standing agents) is in **Settings → Usage & billing**.
- Tool times are measured from this release on; older tool calls count as calls without a time.
- Up to 20,000 runs per query are included (the response says `truncated: true` beyond that).

## API

`GET /api/analytics?range=7d` (or `from=…&to=…`), with optional `source`, `status`
(`running`, `completed`, `failed`), `q` (goal search) and `tz_offset_minutes` (as JavaScript's
`getTimezoneOffset()`, for day boundaries). Any member of the organization can read it.
