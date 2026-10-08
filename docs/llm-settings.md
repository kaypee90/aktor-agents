# Choosing models

An organization sets up the models its agents can use under **Settings → AI model**: any number
of named models, each a provider, a model id, its own API key and the prices budgets count in. One
is the default. Each task can pick its model when it starts and **switch to another while it
runs**, for example starting on a strong model and finishing on a cheaper one. The server's
configuration (`LLM_PROVIDER`, `LLM_MODEL`, `LLM_API_KEY` in `.env`) is always available as the
**Server default** model.

| Provider | Needs | Notes |
|---|---|---|
| **Anthropic** | API key | Claude models. |
| **OpenAI** | API key | Also any OpenAI-compatible service (OpenRouter, Groq, Together, vLLM, LM Studio): set a custom base URL. |
| **Gemini** | API key | Google AI Studio keys. |
| **Ollama** | address | Local models; free per token. The model must support tool calling (qwen3, qwen2.5, llama3.1, mistral-nemo…). |
| **Mock** | nothing | Scripted demo answers, for trying the platform. |

## Setting up models

1. **Settings → AI model** lists the models: the server default and the organization's own, with
   each one's price per million tokens and whether its key is saved.
2. An Admin clicks **Add model**, picks a provider, names the model (e.g. "GPT-5 mini (cheap)"),
   pastes its API key and picks the model from the dropdown, which shows each listed model's price.
   **Load models** asks the provider which models the key can use; ones not on the price list
   (fine-tunes, new releases) appear under "From your account". **Custom model…** takes any other
   model id the provider serves.
3. Optionally a **fast model**: a cheaper one from the same provider for routine work (standing
   agents handling events, history summaries). Planning and real work use the main model.
4. **Prices**, in USD per million tokens. Budgets, spend, cost estimates and analytics are counted
   with these. A listed model is priced from the price list, so there's nothing to enter; **Use a
   different price** sets your own (e.g. a negotiated rate). A model that isn't listed, or any model
   at a custom base URL (another service with its own prices), needs its input and output price
   entered. Local providers are free. A model saved without prices and not on the list falls back
   to the server's prices; the model list flags it.
5. **Test connection** makes one tiny call, so a wrong key, model or address shows up before
   agents depend on it.
6. **Make default** picks the model tasks use when they don't choose one.

## Picking a model for a task

- **When starting:** the task composer has a model picker showing each model's price. The cost
  estimate uses the chosen model's prices. Over the API, pass `"model": "<model id>"` (or
  `"server"`) to `POST /api/tasks` and `POST /api/tasks/preview`.
- **While it runs:** the run page shows the model in use; choosing another switches the whole
  team. Every agent, including ones spawned later, uses it from its next step. Work already done is
  kept, and the switch appears in the run's Activity. Over the API: `POST /api/tasks/{id}/model`
  with `{"model": "<model id>"}`.
- **After it finishes:** fork it from any step onto another model (**Journal & replay → Fork after
  step**, with the model picker next to it). The steps before the fork are copied, so both runs share
  the same start and the diff shows how the models differ.

Workspaces use the organization's default model, and each pipeline stage can run on another one:
pick it in the stage's settings (**Model**), or say so in plain language with mentions ("use
`@claude-fast` for `@triage`"). Only models the organization has set up are accepted. A stage's
helpers run on its model. See [workspaces.md](workspaces.md#mentions).

## Different models for different agents, in plain language

Agents that can spawn see the organization's models in their instructions (a MODELS section with
each model's id, name, "when to use it" note and prices), and `spawn_agent` takes an optional
`model`. So the goal can say which model does what:

> Research the AI bookkeeping market. Use **Careful** for the market analysis, **Quick** for
> collecting competitor pricing, and **Local Qwen** for anything touching our customer list.

In the composer, type `@` to pick a model by its id: mentioning one also switches the task's model
picker to it (a provider, such as `@anthropic`, picks that provider's first model if none is
picked).

The root agent spawns each specialist on the model named. When the goal says nothing, an agent can
still pick one from the models' notes (a cheaper model for routine work, a stronger one for hard
reasoning), or leave it out.

- **Inheritance.** An agent spawned without a model runs on its parent's: a branch started on
  Careful stays on Careful unless told otherwise. Agents not given a model follow the task's model.
- **Switching.** Switching a running task moves the agents that follow the task's model; agents
  given their own model keep it.
- **The runtime decides.** Only models the organization has set up can be chosen (by id or name);
  anything else is refused with the list of valid ids. A spawned agent's cost still comes out of the
  budget its parent gives it, at its own model's prices, so a stronger model drains it faster but
  can't overspend it.
- **Turning it off.** **Let agents choose models** (Settings → AI model, Admins) is on by default.
  Off: agents don't see the models and `model` is refused; every agent runs on the task's model.
- **Seeing it.** An agent given its own model shows it on its card in the agent graph, the spawn
  event names it, and Analytics "By model" shows the spend.

## Who can do what

- Everyone in the organization sees the models and can pick one for the tasks they start, or
  switch a running task (Member role).
- Only Admins add, edit, delete, test or set the default model.
- Keys are encrypted with the secrets master key (`SECRETS_MASTER_KEY`), kept per model, and
  never returned by the API or shown to agents.

## Keys and addresses

- A model's saved key is used only for the provider and address it was saved with. Changing either
  asks for the key again, so a key is never sent somewhere new.
- Without a key of its own, a model uses the server's key, but only for the server's own provider
  at the server's own address.
- `LLM_ALLOW_PRIVATE_BASE_URLS` (default `true`) lets organizations point a model at a private
  address, such as Ollama on this machine. **Turn it off on a shared server**: addresses are chosen
  by organization admins, and private ones reach your internal network.
- `LLM_ALLOW_ORGANIZATION_SETTINGS=false` makes the server's configuration apply to every
  organization; the dashboard then shows it read-only.

## API

| Method | Path | Role | |
|---|---|---|---|
| GET | `/api/llm/settings` | any | The server default, the organization's models (never their keys) and the default. |
| POST | `/api/llm/profiles` | Admin | `{name, description, provider, model, fast_model, base_url, api_key, price_per_input_token_usd, …, make_default}`. |
| PUT / DELETE | `/api/llm/profiles/{id}` | Admin | Edit (a key left out is kept) or delete a model. |
| PUT | `/api/llm/default` | Admin | `{profile_id}`; `"server"` for the server's configuration. |
| PUT | `/api/llm/agent-choice` | Admin | `{enabled}`: whether agents may pick models for the agents they spawn. |
| DELETE | `/api/llm/settings` | Admin | Delete every model and key: back to the server default. |
| POST | `/api/llm/test` | Admin | One tiny call with the given settings: `{ok, message, latency_ms}`. |
| POST | `/api/llm/models` | Admin | `{provider, base_url, api_key, profile_id}`: the provider's model ids. |
| GET | `/api/llm/providers` | any | The providers, what each needs, and suggested models. |
| POST | `/api/tasks`, `/api/tasks/preview` | Member | `model`: the model to run on. |
| POST | `/api/tasks/{id}/model` | Member | Switch a running task. `409` once it has finished: fork it instead. |
| POST | `/api/tasks/{id}/replay` | Member | `model`: for a fork, the model the live part runs on. |
| GET | `/api/tasks/{id}` | any | Includes `model`: the model the task runs on now. |

## The price list

`ModelPriceCatalog` (src/AgentRuntime/LLM/ModelPriceCatalog.cs) holds the standard-tier prices of
the Anthropic, OpenAI and Gemini models, copied from each provider's price list, with the date they
were copied (`AsOf`). It covers input, output and cached-input prices; cached input tokens are
counted at the model's own cached rate. Prices for very long prompts (over 100k–272k tokens,
depending on the model) are higher than the listed ones, which each such model's note mentions.
Dated snapshots ("gpt-4o-mini-2024-07-18") are priced as their model. `GET /api/llm/providers`
returns each provider's listed models with their prices.

The dashboard's model dropdown shows input and output prices in **USD per 1 million tokens**.
The selected model's price details also show cached input, preserving fractional-cent rates
such as $0.075 and $0.005. Sources,
verified on **2026-10-08**:

- [Anthropic API pricing](https://platform.claude.com/docs/en/about-claude/pricing)
- [OpenAI API pricing — Standard tier](https://developers.openai.com/api/docs/pricing)
- [Gemini API pricing — Standard paid tier](https://ai.google.dev/gemini-api/docs/pricing)

These are direct-provider text-token rates, not subscription fees or Batch/Flex/Fast rates.
Output rates include billed reasoning/thinking tokens. Notes identify long-context premiums,
audio input rates, and scheduled promotional changes. Cache writes, cache storage, provider
tools and regional premiums are additional charges; see the official source for each provider.
The listed base rates were confirmed against these sources; GPT-5.5 Pro's long-context rates
and Gemini's long-context cached-input rates are now included in the notes.

The API exposes the same per-million rates and a `pricing_url` for each paid provider.
Runtime price settings and saved overrides remain per-token amounts internally, so the editor
converts per-million entries by dividing by 1,000,000 when saving.

The server's own model is priced from the list too, unless `Llm:PricePerInputTokenUsd` and the
other price settings are set. Update the list (and `AsOf`) when providers change their prices.

## How it works

The runtime still talks to models only through `ILLMProvider`. A task's model is kept on its row
(`Tasks.ModelProfileId`), and a model given to an agent at spawn on the agent itself. At the start
of every step each agent reads its own model, else the task's, and resolves the model's
options (provider, model, key, prices), so the model called, the output caps and the prices budgets
are checked against always match. Each call names its model, and `OrganizationLlmRouter` builds the
provider for it, or hands the call to the server's configured provider. Every call is recorded with
its model, tokens, cost and time (`LlmCalls`), which [analytics](analytics.md) uses to compare
models. Replays serve recorded answers and call no model.
