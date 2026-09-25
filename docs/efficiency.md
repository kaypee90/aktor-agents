# Token efficiency

Long-running agents make every inefficiency recur. A check that costs 5,000 tokens once an hour
is 120,000 tokens a day, forever. The runtime therefore attacks cost in four places, largest
saving first.

## 1. Watches: recurring checks with no LLM

Most recurring work is "look at X, and tell me if Y". A **watch** is that rule, written once by
an agent (or by you), which the runtime evaluates in plain code on a schedule. It works on any
connection that returns JSON; nothing in it is specific to one domain.

For example, failed payments from a payments or orders API:

```json
{
  "name": "Failed payments",
  "source_tool": "billing__get",
  "source_arguments": { "path": "/payments", "query": { "limit": "100" } },
  "items_path": "$.body.data[*]",
  "conditions": [
    { "field": "status", "op": "==", "value": "failed" },
    { "field": "amount", "op": ">=", "value": "100" }
  ],
  "key_field": "id",
  "display_fields": ["id", "customer", "amount"],
  "every_minutes": 15,
  "mode": "notify",
  "urgency": "urgent",
  "message": "{count} failed payment(s): {items}"
}
```

More of the same shape:

| Watching | Rule |
|---|---|
| Deals in a CRM | `days_since_activity >= 7`, `mode: wake_agent` so an agent drafts follow-ups |
| A service status API | `status != "ok"` |
| Support tickets | `priority == "urgent"` and `assignee not_exists` |
| A store | `inventory_quantity < 10` |
| A price feed | `price <= 250` |

- **Zero tokens per check.** The runtime calls the tool, finds the items, and tests the conditions
  itself.
- **Reports only what's new.** An item is reported when it *starts* matching, not every hour while
  it stays low. If it recovers and drops again, it's new again.
- **Two modes:**
  - `notify` sends the alert to the user directly (and on to their channels by urgency);
  - `wake_agent` wakes an agent, but only then, and with only the matching items, for when a
    match needs judgement (e.g. "work out a reorder quantity").
- **Safe by construction.**
  - Watches may only call **read-only** tools, since they run unattended.
  - Conditions are data, not code: a JSONPath subset (`$`, `.name`, `['name']`, `[n]`, `[*]`)
    plus comparisons `< <= > >= == != contains not_contains exists not_exists`, all of which must
    hold.
  - JSON inside strings (an HTTP body, MCP text) is parsed along the way.
- **Checked at creation.** A dry run reports `items_found` and `matching_now`, so a wrong path is
  caught immediately instead of silently never matching.
- **Failures are visible.** After 3 failed checks in a row, the target agent is told once.
- **Durable**, like every trigger: it runs on reminders and survives restarts.

Agents are instructed to prefer `create_watch` over `create_schedule` whenever the check is a
clear condition. The workspace header shows the running total of LLM calls avoided.

In the live test, a watch polled a JSON API every minute. It reported the one matching item once,
then only the second item when that started matching too, and the agents spent no tokens across
all the checks.

## 2. Prompt caching

- **Stable prefix.** The system prompt is built with its stable sections first and the changing
  ones (status, usage, time, summary) last, so the long stable prefix is identical call after call.
- **Anthropic:** three cache breakpoints (end of the tool list, end of the stable system prompt,
  latest message), so within a multi-step turn each call reads everything before it from cache, at
  about a tenth of the price.
- **OpenAI:** caches stable prefixes automatically; the reordering is what makes that work.
- **Pricing:** cached and cache-write tokens are costed at their own rates (`CachedInputPriceFactor`,
  `CacheWritePriceFactor`; defaults follow each provider's pricing) and shown as the "% cached"
  readout.

## 3. Model routing

Set `LLM_FAST_MODEL` (e.g. `claude-haiku-4-5-20251001` or `gpt-4o-mini`) and routine work moves to
it: standing agents handling events, simulation residents, and history summaries. Routine calls
also get a smaller output cap (`FastMaxOutputTokens`). The coordinator, which plans, and one-shot
workers, which do the substantive work, stay on `LLM_MODEL`. Costs are tracked at each tier's own
price (`FastPricePerInputTokenUsd`, `FastPricePerOutputTokenUsd`).

## 4. Context compaction

Every LLM call resends the agent's history, so history length is a direct multiplier on cost.

- **Standing agents.** Once history outgrows the agent's window, older entries are folded into a
  rolling summary, which the agent sees under *EARLIER CONTEXT*.
- **Task agents.** Once history passes `CompactAboveTokens` (40k by default), all but the last
  `CompactKeepRecentEntries` are summarized.
- **How the summary works.**
  - Summaries use the fast tier and keep facts, decisions, ids, numbers and open items.
  - If the model doesn't answer, an excerpt is kept instead, so compaction never loses work
    outright.
  - Cuts happen only where no tool call is separated from its result, which keeps the journal
    valid for crash recovery.
- **Residents** keep their cheaper sliding window plus notes.

## Settings

| Setting | Default | |
|---|---|---|
| `Llm:FastModel` (`LLM_FAST_MODEL`) | empty | Cheaper model for routine work. Empty: use `Llm:Model`. |
| `Llm:FastPricePerInputTokenUsd` / `...Output...` | main prices | For cost tracking. |
| `Llm:FastMaxOutputTokens` / `Llm:MaxOutputTokens` | 1024 / 4096 | Output caps per tier. |
| `Llm:CachedInputPriceFactor` / `Llm:CacheWritePriceFactor` | per provider | Anthropic 0.1 / 1.25; OpenAI 0.5. |
| `Llm:CompactAboveTokens` | 40000 | Task-agent compaction threshold. |
| `Llm:CompactKeepRecentEntries` | 24 | Entries kept verbatim. |
| `Workspaces:StandingContextWindow` | 40 | Standing agents compact beyond this. |
| `Integrations:MaxEnabledToolsPerConnection` | 20 | Tool definitions cost tokens on every call. |

## Tests

- `WatchEvaluatorTests` covers paths, operators and validation.
- `WatchTests`:
  - a watch alerts once per newly matching item, with no LLM call during checks;
  - `wake_agent` sends only the matches;
  - write tools are refused.
- `TokenEfficiencyTests` (unit): prompt ordering and cache boundary, Anthropic breakpoints and
  usage, OpenAI cached tokens, cost maths, tier selection.
- `TokenEfficiencyTests` (integration):
  - the monitor uses the fast model and the coordinator the main model;
  - history compacts into a summary that later calls carry;
  - a task agent keeps working after compaction.
