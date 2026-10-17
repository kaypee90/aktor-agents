# Aktor as a platform to build on

Ordinary web applications are being rebuilt as agentic systems. Aktor's direction is to be the
infrastructure those systems run on. A builder brings a business use case; Aktor provides the
runtime (actors, durability, budgets, safety, audit, multi-tenancy) and the building blocks. The
builder ships a product to their own customers without touching Aktor's internals.

This page records what that means, what's built, and what's next.

## The test

Aktor is infrastructure when all of these hold:

1. A developer builds a real solution using **only** the dashboard's builder, or a package with the
   CLI, the public `/api/v1` and the SDKs, with no change to Aktor's code.
2. It installs into a fresh organization in minutes, asking only for inputs and secrets.
3. Its own customers use it through an embed, isolated from each other by the runtime.
4. A new version deploys without breaking work in progress.
5. A second, unrelated solution is built the same way with no platform changes.

[samples/solutions/property-mgmt](../samples/solutions/property-mgmt) and
[samples/solutions/helpdesk](../samples/solutions/helpdesk) are two unrelated solutions built with
the same primitives and no platform changes. The integration tests install and run both, and a unit
test validates every package under `samples/solutions` on each build. A platform change that would
break a published solution fails CI. Packages also declare `requires_platform`, so an older server
refuses a package it can't run.

## What a builder gets

| Need | Where it lives |
|---|---|
| Build a solution without code: forms for every part, checked as you go, deployed from the page | [solutions.md#the-builder-no-cli-or-yaml-needed](solutions.md#the-builder-no-cli-or-yaml-needed) |
| Package a solution, install, upgrade, uninstall, version history | [solutions.md](solutions.md) |
| A data model the agents work on: checked writes, per-field indexes, record triggers and webhooks to your backend | [solutions.md#data-entities-and-records](solutions.md#data-entities-and-records) |
| Put agents in front of your own customers: tokens or your own sign-in (JWT), the embed widget, the apps API, allowed origins and rate limits | [solutions.md#end-users](solutions.md#end-users) |
| Share and reuse: a registry of signed, versioned packages | [solutions.md#the-registry](solutions.md#the-registry) |
| Charge your customers: cost per end user, workspace or day, and monthly caps | [solutions.md#usage-and-billing](solutions.md#usage-and-billing) |
| Prove it works: evals in CI | [solutions.md#evals](solutions.md#evals) |
| A stable contract and a versioned platform | [api-versioning.md](api-versioning.md) |
| Tooling | the `aktor` CLI and TypeScript SDK in [sdk/typescript](../sdk/typescript); the Python SDK in [sdk/python](../sdk/python) |
| Integrations in any language | remote plugins with servers for Node and Python ([plugins.md#remote-plugins](plugins.md#remote-plugins)), MCP servers, OpenAPI imports |
| Mix code and agents | action and script stages, compiled watches ([workflows.md](workflows.md), [efficiency.md](efficiency.md)) |
| Governance your customers can trust | budgets, safety policies, approvals, the hash-chained audit log, replay ([safety.md](safety.md), [replay.md](replay.md)) |
| Durability | the step journal and durable mailboxes ([durability.md](durability.md)) |
| A guided tour | the dashboard's **/docs** page |

## What the runtime still owns

Packages declare; the runtime decides. A solution can't grant itself tools, budgets or access
beyond what an Admin could configure by hand. Its records and its end users are scoped by the
organization, and by the end user, in every query. Its agents run under the same budgets,
approvals and audit as any other. That's the property builders inherit, and their customers rely on.

## Next

Built so far: packaging and upgrades, the data model with indexes and triggers, end users with
tokens or federated sign-in, the registry, usage and caps, evals, remote plugins in Node and Python,
and the CLI and SDKs. What's left:

1. **A real-model baseline for the samples' evals.** Every test so far runs on the Mock model; a
   scheduled CI job on a real model would catch behaviour regressions, not just plumbing.
2. **Registry across servers.** Today the registry is per server; federating it (pulling signed
   packages from another Aktor server) would let a builder publish once for many deployments.
3. **A catalog for remote plugins**, so an organization can list the plugin services it runs and
   add them to a solution by name.
4. **Entities in your own database** behind the same `data__` tools, for data that must stay where
   it is.
5. **Sharding the agent registry by tenant**, SSO/OIDC for organizations, and usage-based plans
   ([platform.md](platform.md)).
6. **Connectors verified against live accounts.** Several are tested only against simulated
   responses.
