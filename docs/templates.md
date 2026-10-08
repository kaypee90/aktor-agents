# Workspace templates

Templates are real-world pipelines to start from. Each sets up a workspace with its pipeline, a
safety policy, its triggers (webhooks and schedules), and a sample to try straight away. They're
ordinary pipelines afterwards: change any stage in plain language or on the canvas, connect your
own services, and add triggers.

Open **Templates** in the dashboard, filter by category, and **Use template**. With **Try it now
with the sample** on, a webhook template sends its sample payload through its own webhook (as the
real service would), and other templates start a first run with their sample input. Under the Mock
model every template runs end to end; with a real model, each stage's agent follows its
instructions.

| Template | Category | Pipeline | Triggers | Safety |
|---|---|---|---|---|
| **Incident response** | Operations | Triage → Logs ∥ Metrics ∥ Deploys → Diagnose → Remediate | Webhook "Incoming alerts" | SemiAutonomous; rollbacks need approval ([incident-response.md](incident-response.md)) |
| **Support ticket triage** | Support | Classify → Find the answer → Draft reply → Quality check | Webhook "New tickets" | SemiAutonomous (writes to a connected helpdesk need approval); results are warnings |
| **Market research report** | Research | Brief → Market size ∥ Competitors ∥ Customers → Write report → Fact check | Run by hand | Autonomous |
| **Pull request review** | Engineering | Understand the change → Correctness ∥ Security ∥ Tests & docs → Combined review | Webhook "Pull requests" | SemiAutonomous (posting comments needs approval) |
| **Lead research & outreach** | Sales | Company research ∥ Contact research → Qualify → Draft email | Webhook "New leads" | SemiAutonomous (sending or CRM writes need approval) |
| **Content production** | Marketing | Research → Outline → Draft → Edit ∥ SEO review → Final version | Run by hand | Autonomous |
| **Weekly competitive intelligence** | Research | Scan competitors (3 helpers) → What it means → Digest | Schedule: Mondays 08:00 UTC | Autonomous; remembers last week's facts |
| **Contract review** | Legal & finance | Summarise → Risk review ∥ Obligations & dates → Negotiation memo | Run by hand | Autonomous; not legal advice |
| **Candidate screening** | People | Profile → Strengths ∥ Gaps & questions → Interview brief | Webhook "New applications" | SemiAutonomous; job-related criteria only, a recruiter decides |

Common choices across them:
- **Parallel branches** for independent work, merged by one stage.
- **Keep going if it fails** on parallel research branches, so one missing source doesn't stop the
  run; the merging stage is told what failed.
- **No retries on output stages** that might act (a rollback, a memo a person relies on).
- **Your context in knowledge:** templates refer to the workspace's knowledge for things like the
  ideal customer profile, a legal playbook, a brand voice or a role's requirements. Add them under
  **Skills & knowledge** and every run uses them.

## Your organization's templates

**Save as template** on a workspace adds it to **Templates** for everyone in the organization,
marked *Yours*: its goal, pipeline, triggers, safety policy, daily budget and the list of
integrations, without secrets, knowledge, runs or files. Creating a workspace from it works like a
built-in template; integrations are added for Admins (an integration that needs a secret reports
that it needs one, and is added again under **Integrations** with it).

- **Download** saves a template as a JSON file; **Import template** adds one from such a file, e.g.
  on another server. A file never carries secrets, whatever it contains.
- Admins can delete the organization's templates; workspaces made from one aren't affected.

API: `POST /api/workspaces/{id}/export-template` (`{name, description, category}`),
`GET /api/workspace-templates/{id}/download`, `POST /api/workspace-templates/import` (the file as the
body), `DELETE /api/workspace-templates/{id}` (Admin). `GET /api/workspace-templates` lists built-in
templates, then the organization's (`custom: true`), and `POST /api/workspaces/from-template`
takes either id.

## Adding a template

Templates live in `src/AgentRuntime/Workspaces/WorkspaceTemplates.cs` (incident response) and
`WorkspaceTemplateCatalog.cs` (the rest). A template is a `WorkspaceTemplate`: id, name, category,
description, goal, pipeline, safety policy, and optional connections, webhooks (with a sample
payload), schedules (5-field UTC cron) and sample input. Add it to `WorkspaceTemplates.All`.
`WorkspaceTemplateTests` checks every template: its pipeline is valid with a single output stage,
its samples parse, its schedules are valid cron, and it has something to try it with.

## API

| Method | Path | |
|---|---|---|
| `GET` | `/api/workspace-templates` | Each template: id, name, category, description, goal, autonomy, sample input, stages (id, name, inputs), connections, webhooks, schedules |
| `POST` | `/api/workspaces/from-template` | `{template, name?, use_demo_system?}` → `{workspace_id, connections, webhooks: [{name, path}], schedules, sample_input}` |
| `POST` | `/api/workspaces/{id}/simulate-alert` | `{payload?}`: sends the template's sample (or this payload) through its first webhook |
