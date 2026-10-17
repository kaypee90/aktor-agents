# API versioning

Aktor's REST API has two prefixes for the same endpoints:

| Prefix | Who it's for | Stability |
|---|---|---|
| **`/api/v1/...`** | Builders: SDKs, the `aktor` CLI, your backend, integrations | **The contract.** Changes within v1 are additive only. |
| `/api/...` | The dashboard | Same endpoints, but free to change with the dashboard. |

A request to `/api/v1/x` is served by `/api/x` and answered with an `Aktor-Api-Version: 1` header.
New code should use `/api/v1`. Everything added for building on the platform (solutions, records,
end users) is documented under `/api/v1` only.

## What "additive only" means within v1

These can happen in v1:
- new endpoints;
- new optional request fields;
- new response fields (clients must ignore fields they don't know);
- new enum values in responses (clients must accept values they don't know);
- new error messages for existing status codes.

These can't happen in v1, and would mean a v2 (served alongside v1 while clients move):
- removing or renaming an endpoint, a field or an enum value;
- making an optional request field required;
- changing a field's type or meaning;
- changing which status code an outcome returns.

## Conventions

- JSON in snake_case, enums as their names (`"Completed"`).
- Errors are `{ "error": "…" }`; validation errors add `"errors": ["…", …]` listing every problem.
- `401` means no or bad credentials. `403` means your role doesn't allow it. `404` also covers
  "exists, but in another organization".
- Retries: `GET`, `PUT` and `DELETE` are safe to repeat. Creates that accept an `Idempotency-Key`
  header (records) return the first result for a repeated key.
- The OpenAPI description is at `/openapi/v1.json`.

## The solution platform version

Solution packages can declare `requires_platform` (e.g. `">=1.1.0"`). The server's version is
`SolutionValidator.PlatformVersion`. It follows the same rule as the API: minor versions add
features, and only a major version would remove or change one.

| Version | Added |
|---|---|
| 1.0.0 | Solutions: packages, entities and records, record triggers, end users, the assistant, evals, webhooks |
| 1.1.0 | `requires_platform`, `assistant.allowed_origins`, webhook triggers updated in place on upgrade |
| 1.2.0 | `indexed` fields, `end_users.jwt` (your own sign-in), `assistant.monthly_cost_limit_usd_per_user`, installing from the registry | Every package under `samples/solutions` is validated on
each build, so a change that would break a published package fails CI.

## Credentials

| Credential | Header | Reaches |
|---|---|---|
| API key | `Authorization: Bearer ak_…` | the organization's API, by the key's role |
| Session | cookie (the dashboard) | the same, for a signed-in person |
| End-user token | `Authorization: Bearer aeu_…` | `/api/v1/apps/{installId}/…` only, for one end user of one solution ([solutions.md](solutions.md#end-users)) |
| Your own JWT | `Authorization: Bearer <JWT>` | the same, for a solution that declares `end_users.jwt` ([solutions.md](solutions.md#your-own-sign-in)) |
