# Skills

Skills teach your agents how your organization does particular kinds of work: how you write a
postmortem, your API conventions, your report format. A skill is written once and then used by every
agent of the organization, in tasks and workspaces alike.

## How agents use them

- Every agent's prompt has a **SKILLS** section listing each enabled skill's name and description
  (up to `Skills:MaxListedInPrompt`, 40). The description is all an agent sees up front, so a large
  library costs one line per skill.
- When an agent's work matches a description, it calls **`load_skill`** to read the full
  instructions and the list of the skill's files, and **`read_skill_file`** for a resource file the
  instructions point to.
- Both tools are read-only. Every agent gets them while its organization has at least one enabled
  skill; simulation residents don't.
- **Skills are instructions, not permissions.** A skill never grants tools, budget or access. The
  runtime enforces those as always.
- Nothing in a skill is executed by the runtime. A script in a skill is only text an agent can read
  (and run through `shell_exec`, in its sandbox, if it has that tool).
- Skills belong to one organization: other organizations' agents and users can't list, load or
  read them, even by name.
- **Workspace skills.** A skill can belong to one workspace instead of the whole organization. Only
  that workspace's agents list and load it, on top of the organization's skills. Where both have a
  skill with the same name, the workspace's version wins for its agents (turn it off and they fall
  back to the organization's). Tasks and other workspaces never see it.
- **Asking for a skill by name.** Write `@skill:<name>` in a task, a run's input or a stage's
  instructions (the text boxes suggest skills as you type `@`). An agent whose goal or context names
  a skill sees it first in its SKILLS list, marked as asked for, and is told to load it before
  anything else, even when the library is longer than the prompt lists.
  See [workspaces.md](workspaces.md#mentions).
- A replay serves `load_skill` results from the recorded run, so it sees the skill as it was then.

## The format

The [Agent Skills](https://agentskills.io) layout: a `SKILL.md` with frontmatter, optionally in a
folder with resource files.

```markdown
---
name: incident-postmortems
description: How we write incident postmortems. Use when asked for a postmortem or an incident write-up.
---

## Steps
1. Build the timeline from the alert, deploys and logs.
2. …

See reference/template.md for the layout.
```

| Field | Rules |
|---|---|
| `name` | Lowercase letters, digits and hyphens; 1–64 characters; unique in the organization |
| `description` | Required; at most 1,024 characters. Say what the skill does *and when to use it*: agents decide from this alone |
| Body | The instructions, in Markdown; at most 100,000 characters (move detail into files) |
| Files | Text files only (binaries are skipped), at most 50 and 2 MB in total, paths inside the skill |

## Adding skills

**Skills** in the dashboard sidebar (Admins; everyone else can read them). The **Scope** picker at the
top chooses whose skills you're looking at: the whole organization's, or one workspace's own. A
workspace's **Skills & knowledge** tab links straight to its scope.
- **Write a skill:** name, description, instructions, and optional resource files, in the editor.
- **Upload .md / .zip:** a `SKILL.md`, or a zip with `SKILL.md` at its root or in one top-level folder
  plus its files. Uploading a skill whose name exists saves it as a new version.
- Each skill can be edited (saved as a new version), turned off (agents stop seeing it), downloaded
  (as `SKILL.md`, or a zip when it has files) and deleted.

## API

| Method | Path | |
|---|---|---|
| `GET` | `/api/skills` | Name, description, version, enabled, file count |
| `GET` | `/api/skills/{name}` | Including instructions and files |
| `GET` | `/api/skills/{name}/download` | `SKILL.md`, or `?format=zip` |
| `POST` | `/api/skills` | Admin. `{name, description, instructions, files?: [{path, content}]}`; 409 if the name exists |
| `PUT` | `/api/skills/{name}` | Admin. `{description, instructions, files?}`, saved as a new version; files are kept when omitted |
| `POST` | `/api/skills/upload` | Admin. Multipart field `file`: a `.md` or `.zip`; replaces a same-named skill as a new version |
| `PATCH` | `/api/skills/{name}` | Admin. `{enabled}` |
| `DELETE` | `/api/skills/{name}` | Admin |

Add `?workspace={id}` to any of them to work on that workspace's own skills (`404` for a workspace of
another organization). Without it, they work on the organization's.

## Tests

- `SkillPackageTests` (unit): frontmatter, invalid skills, zips (folder layout, binaries and
  hidden files dropped, size and file limits), paths escaping the skill, and the write-then-zip
  round trip.
- `SkillsTests`: an agent's prompt lists only its organization's enabled skills; it loads one and
  reads a file; another organization's skill can't be loaded by name; with no skills nothing is added.
- `SkillsApiTests` (real API and Postgres): write, edit, upload `.md` and `.zip`, download, turn
  off and delete; only Admins change skills; other organizations see nothing.
- `WorkspaceScopeTests` (unit): workspace agents see their workspace's skills on top of the
  organization's, a workspace skill overrides the organization's, others can't load it.
- `WorkspaceScopeApiTests`: `?workspace=` keeps the two apart, deleting the workspace's version
  leaves the organization's, and another organization gets `404`.
