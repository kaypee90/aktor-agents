# Solutions: what changed

Everything added or changed to make Aktor a platform others build their own agentic products on.
How to use it is in [solutions.md](solutions.md); where it's going is in [platform-builders.md](platform-builders.md).
This page is the record of what was built, in the order it was built.

Solution platform version: **1.2.0** (see [api-versioning.md](api-versioning.md)).

## Upgrading a server

- **Rebuild and restart** (`docker compose up --build`). One database migration, `AddSolutions`,
  is applied automatically when the API starts. It only adds tables, so existing data is untouched.
- **Back up `SECRETS_MASTER_KEY`** (or `keys/secrets-master.key`). Besides encrypting the vault, it
  now signs records bindings, end-user tokens and registry packages. A new key invalidates all three.
- Nothing else is required. Every existing API, page, task and workspace works as before.

## 1. Solutions (platform 1.0.0)

**Packages.**
- **What a package is:** a solution is a folder with an `aktor.yaml` (YAML or JSON) plus files.
  Workspaces, evals and the assistant can live in their own files (`file:`), and long instructions
  too (`instructions_file:`). The server reads it; nobody needs a YAML library.
- **What the manifest holds:**
  - inputs, which workspaces see as variables;
  - connectors, whose secrets are asked for at install and never packaged;
  - entities, workspaces, skills, knowledge, an assistant, seed records, webhooks and evals.
- **Validation:** every problem is listed before anything is installed. It uses the same rules a
  workspace applies by hand.
- **Lifecycle:** install, upgrade and uninstall, with version history.
  - An upgrade must raise the version (`force` redeploys), and `dry_run` reports what would change.
  - An upgrade applies only what the package changed, so people's edits in a workspace survive.
  - Runs in progress finish on their old pipeline version.
  - Uninstall archives the workspaces; `purge` deletes the records too.

**Data.**
- **Entities and fields:** types `string text integer number boolean date datetime enum ref
  end_user json`. Every write, whoever makes it, is validated and normalized. Refs must point at a
  record the writer can see, and a referenced record can't be deleted.
- **Tools for agents:** each workspace with `data:` access gets a `data` connection (the
  `aktor-records` plugin): `data__describe_data` and `list_/get_/create_/update_/delete_<entity>`.
  Creates are idempotent, so a replay finds the record it made, and updates support
  `expected_version`.
- **Record triggers:** a new trigger kind, `Record`, starts a run when a record is created,
  updated or deleted, optionally only when conditions match. Changes go through an outbox written
  in the record's own transaction, and each matching trigger fires once per change.
- **Records API for your backend:** `/api/v1/solutions/{id}/records/{Entity}`, with
  `Idempotency-Key` on creates and `If-Match` on updates.
- **Webhooks to your backend:** signed (`X-Aktor-Signature: sha256=…`), retried with backoff.
- **Seed data:** inserted at first install only. Its records can refer to each other, and they
  don't fire triggers.

**End users.**
- **Tokens:** `aeu_…` tokens are issued by your backend (`POST …/end-user-tokens`). They're signed
  and expire, and they work only on the apps API.
- **The assistant:** each end user's conversation is a task, with a records connection bound to
  that user. For entities with an `end_user` field, they see, change and link only their own records.
- **Apps API:** `/api/v1/apps/{installId}/…` for conversations and their own records, open to any
  origin (the token is the credential).
- **Embed widget:** `/embed.js`, in a shadow DOM, in light and dark, with token refresh through
  your URL.

**Around it.**
- **Evals:** they run workspaces for real and check the status, the summary and the records written.
- **API versioning:** `/api/v1/...` is the stable contract, answered with `Aktor-Api-Version: 1`.
- **Remote plugins** (`remote-plugin`): an integration in any language as an HTTP service.
- **Tools:** the `aktor` CLI (init, validate, deploy, dev, test, records, token, status,
  uninstall), solutions and records in the TypeScript SDK with `AktorApp`, and a new Python SDK.
- **Dashboard:** the Solutions page (installs, workspaces, records, the assistant with a live
  widget, evals, history).
- **Sample:** `samples/solutions/property-mgmt`.

## 2. Gaps closed (platform 1.1.0)

- **Filters run in SQL** over the whole table, no longer over the newest 2,000 records.
- **`assistant.allowed_origins`:** pages on other sites get 403. Backend calls without an `Origin`
  aren't affected.
- **Rate limits** per end user hold across every server, through one grain per user.
- **Webhook triggers are updated in place** on upgrade, so their URL and secret stay the same.
- **Records are read-only after uninstall** (writes get 409); they can still be read and exported.
- **Assistant-only connectors** get their secrets from the vault, asked for at install and not
  asked again on upgrade.
- **The dashboard edits records:** create, edit and delete, with conflict protection.
- **`requires_platform`:** an older server refuses a package it can't run, and every sample is
  validated on each CI build.
- **A trigger can't feed itself:** a change made by a workspace's own run doesn't start another run
  of that workspace.
- **A second sample:** `samples/solutions/helpdesk`, unrelated to the first and built the same way.

## 3. Builder-platform features (platform 1.2.0)

- **Registry:**
  - Commands: `aktor publish | search | install | yank`.
  - A package name belongs to the organization that first published it.
  - Versions are immutable and only go up. Each is signed, and checked at every install.
  - Packages can be visible to your organization only, or public on the server.
  - Install or upgrade from the registry with `{ "package": "name@version" }`.
- **Usage and billing:** `GET …/usage?by=end_user|workspace|day` (with CSV), from the runtime's own
  metering. `assistant.monthly_cost_limit_usd_per_user` caps an end user (402 when over).
- **Your own sign-in:** `end_users.jwt` accepts your identity provider's JWTs, checked against its
  published keys (RS/PS/ES). `none` and shared-secret tokens are refused, and rotated keys are
  picked up automatically.
- **Per-field indexes:** `indexed: true` creates a database index, drops it when no longer asked
  for, and rebuilds it when the field's type changes.
- **Node remote plugins:** `@aktor/sdk/plugin`, alongside Python's `aktor.Plugin`.

## 4. The builder: no CLI or YAML needed

- **Solutions → New solution** and **Edit in builder** open a visual editor for every part of a
  solution, checked against the server as you type.
- **Deploy from the page:** no download needed. Inputs and secrets are asked for, then the webhook
  URLs created are shown.
- **Download is optional:** the YAML and a `.zip` of the package, for git or the CLI. **Edit YAML**
  applies text back to the forms. Drafts are saved in the browser.
- **New endpoints:** `POST /api/v1/solutions/parse` (a package as structured JSON) and
  `GET /api/v1/solutions/{id}/package` (the package an install came from).

## 5. Fixes along the way

- **Watch tool calls** now carry the organization, so watches on records work for every organization.
- **The Mock model** answers end users' messages plainly, instead of quoting the assistant's
  hidden instructions.
- **The deploy dialog** shows the webhook URLs a deploy created. Before, they were only in the CLI
  output and the workspace chat.
- **CLI scripts were ignored by git:** `.gitignore` ignored `sdk/typescript/bin/`, so the existing
  `aktor-acp.mjs` was never tracked. That folder is now included.
- **Small fixes:** the `aktor init` scaffold's YAML, readable JSON in watch arguments, and numbers
  of any form accepted in number fields.

## Documentation and pages

- **New docs:**
  - [solutions.md](solutions.md): the guide;
  - [platform-builders.md](platform-builders.md): direction and what's next;
  - [api-versioning.md](api-versioning.md): the contract and platform versions;
  - this page.
- **Updated docs:** [guarantees.md](guarantees.md), [plugins.md](plugins.md) (remote plugins),
  [workspaces.md](workspaces.md) (record triggers), [platform.md](platform.md),
  [roadmap.md](roadmap.md), [manual-testing.md](manual-testing.md) (tests SO1–SO10), the README,
  and both SDK READMEs.
- **In the dashboard:** a public **/docs** page, the guided tour; the homepage (**/welcome**) with
  "Build on Aktor"; and **Documentation** in the sidebar.

## Tests

The solutions work added these tests:
- **Unit:** `SolutionTests`, `EndUserJwtTests` and `RemotePluginTests`.
- **Integration**, against the real API and Postgres: `SolutionsApiTests` and
  `SolutionPlatformApiTests`.
- **SDKs:** Python SDK tests and Node plugin tests.
- **CI:** an `sdks` job.

At the time of writing, all 475 unit tests and 159 integration tests pass. Every automated run uses
the Mock model; testing on a real model is still to do ([platform-builders.md](platform-builders.md#next)).
