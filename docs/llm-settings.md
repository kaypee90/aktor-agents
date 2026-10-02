# Choosing the AI model

Each organization picks the model its agents reason with in the dashboard, under
**Settings → AI model**. Without a choice of its own, an organization uses the server's
configuration (`LLM_PROVIDER`, `LLM_MODEL`, `LLM_API_KEY` in `.env`).

| Provider | Needs | Notes |
|---|---|---|
| **Anthropic** | API key | Claude models. |
| **OpenAI** | API key | Also any OpenAI-compatible service (OpenRouter, Groq, Together, vLLM, LM Studio): set a custom base URL. |
| **Gemini** | API key | Google AI Studio keys. |
| **Ollama** | address | Local models; free per token. The model must support tool calling (qwen3, qwen2.5, llama3.1, mistral-nemo…). |
| **Mock** | nothing | Scripted demo answers, for trying the platform. |

## In the dashboard

1. **Settings → AI model** shows the model in use, where it comes from, and the prices budgets
   are counted with. The task composer shows the model too, with a link here.
2. An Admin picks a provider, pastes its API key, and chooses a model. **Load models** asks the
   provider which models the key can use. Any model id the provider serves can also be typed.
3. Optionally, a **fast model**: a cheaper one for routine work (standing agents handling events,
   history summaries). Planning and real work always use the main model.
4. **Prices**, in USD per million tokens as providers publish them. Budgets, spend and cost
   estimates are counted with these, so copy them from the provider's price list. Left blank,
   the server's prices are used (local providers are free).
5. **Test connection** makes one tiny call with the settings, so a wrong key, model or address
   shows up before agents depend on it. **Save** applies the change from each agent's next step,
   running tasks included.

**Use server default** goes back to the server's configuration and deletes the organization's
saved keys.

## Who can do what

- Everyone in the organization can see which model is in use.
- Only Admins can change it, test it or list models.
- Keys are encrypted with the secrets master key (`SECRETS_MASTER_KEY`), kept per provider (so
  switching back needs no new key), and never returned by the API or shown to agents.

## Keys and addresses

- Without its own key, an organization uses the server's key, but only for the server's own
  provider at the server's own address. A key is never sent to an address an organization chose.
- `LLM_ALLOW_PRIVATE_BASE_URLS` (default `true`) lets organizations point a provider at a private
  address, such as Ollama on this machine. **Turn it off on a shared server**: base URLs are
  chosen by organization admins, and private addresses reach your internal network.
- `LLM_ALLOW_ORGANIZATION_SETTINGS=false` makes the server's configuration apply to every
  organization; the dashboard then shows the model read-only.

## API

| Method | Path | Role | |
|---|---|---|---|
| GET | `/api/llm/settings` | any | The model in use, the server default, and the organization's settings (never the key). |
| PUT | `/api/llm/settings` | Admin | `{provider, model, fast_model, base_url, api_key, price_per_input_token_usd, …}`. `api_key` is optional once saved. |
| DELETE | `/api/llm/settings` | Admin | Back to the server default. |
| POST | `/api/llm/test` | Admin | Same body as PUT; one tiny call, returns `{ok, message, latency_ms}`. |
| POST | `/api/llm/models` | Admin | `{provider, base_url, api_key}`: the provider's model ids. |
| GET | `/api/llm/providers` | any | The providers, what each needs, and suggested models. |

## How it works

The runtime still talks to models only through `ILLMProvider`. Every call carries the
organization's id; `OrganizationLlmRouter` resolves the organization's settings (cached for 30
seconds, refreshed at once on save) and builds the provider for them, or hands the call to the
server's configured provider when the organization chose nothing. Agents re-read the settings at
the start of every step, so the model, the output caps and the prices budgets are checked against
always match the model actually called. Replays serve recorded answers and are unaffected.
