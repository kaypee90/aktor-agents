# Tasks: conversations, files and documents

A task is a conversation with a team of agents. You describe the outcome you want and can attach
files. The team plans and does the work, then answers with a summary and the files it made. From
there you keep going in the same conversation: refine the result, ask for another format, attach
more data, or give the team more budget to finish. Nothing it already did or learned is lost.

## The chat view

Every task opens as a chat (the default; `?view=agents` opens the agent graph instead):

- **Your messages**, with the files you attached shown as cards.
- **The team's answers**, rendered as Markdown, with a card for each file the agents wrote in
  that round. Each answer has **Copy**, **Show work** (the steps behind it, by agent: spawns, tool
  calls, messages, files) and **Agents** (jump to the graph).
- **A working card** while the team is busy: how many agents are working, their latest steps, and
  **Stop**.
- **Suggestions** after an answer: "Turn this into a Word document", "Build a slide deck from
  this" and others. Click one to put it in the composer.
- **Previews.** Click any file to open it in a panel beside the chat (full screen on narrow
  screens). See [Previews](#previews).

The **Chat | Agents** switch in the header moves between the conversation and the live graph,
activity, result and estimate panels. Both views show the same task.

### Starting a task

The home page composer takes a goal, files (button, drag and drop, or paste), the model, and
**Options** for budget, team shape and delivery. Enter runs it; Shift+Enter adds a new line.
**Estimate cost** previews the team and cost first ([preview.md](preview.md)).

Files picked before the task exists are uploaded straight away to a staging area
(`POST /api/uploads`). When the task starts (`attachments` on `POST /api/tasks`), they move into
the task's workspace under `attachments/`. The root agent then starts with their names, paths and
the beginning of their text. A task can start from files alone. Its goal is then "Review the
attached files and tell me what's important in them." Staged files that no task claims are deleted
after a day. Upload ids belong to the organization that made them.

## Follow-ups

Anything you send after the first message is a follow-up for the task's root agent:

- **While the task is running**, the root reads it at its next step and folds it into its work.
- **After the task finished** (completed, partial, failed, timed out or stopped), the runtime
  **reopens** the root agent. It keeps its whole history: its conversation, a summary of anything
  compacted, its completed work, and every file in the task's workspace. It has a new round of
  budget and time to work in. It can start new agents (the earlier ones have finished and can't be
  messaged). When it's done it reports again, and the task's result is rebuilt.

Follow-ups also become part of the root's standing instructions, under its goal. Summarizing a
long history can't lose them.

Each round's budget is the task's own budget again unless you set another (`budget` on the
request). It's granted **on top of what was already spent**, so usage and cost totals keep
counting across rounds. It's capped by the server's `TaskBudgetCeiling` like any budget. Only a
person, through the API, grants it. No agent and no message from another agent can.

## Continuing with more budget

When an answer stops before the work is done, the chat shows **Not finished yet** with what was
left. The **Continue** card then lets you choose the budget for the rest:

- **Same again**, **2×** or **5×** the task's budget, or your own cost, tokens, time and tool-call
  limits (the server's maximum is shown under each).
- An optional note ("Keep the workbook to three sheets").

**Continue** sends the root agent what was left, your note, and the new budget. It carries on from
where it stopped, building on the work and files it already has. The card appears after a partial
result, a failure, a timeout, or when you stopped the task. A running task takes messages instead
(`409` if you try to continue it).

Over the API:

```bash
curl -X POST http://localhost:5080/api/tasks/$TASK/continue \
  -H "Content-Type: application/json" \
  -d '{"budget": {"max_cost_usd": 4, "max_tokens": 200000}, "note": "Keep it short."}'
```

Limits left out come from the server's default budget. `GET /api/tasks/{id}` returns the task's
`budget` and the server's `budget_ceiling`.

## Attachments

Attach any kind of file to the first message or to a follow-up: up to 10 per upload, 25 MB each
(`ATTACHMENT_MAX_FILES`, `ATTACHMENT_MAX_BYTES`). Files are saved under `attachments/` in the
task's sandboxed workspace. A name that's already taken gets a suffix, so nothing is overwritten.

The root agent gets each file's path, type and size, plus the beginning of its text (up to about
6,000 characters a file and 16,000 in all). The excerpts go in its history; the file list goes in
its standing instructions. Agents read whole files with `filesystem_read`, which returns:

| File | What the agent reads |
|---|---|
| Text, code, Markdown, CSV, JSON, HTML | The file as it is |
| PDF | Its text, page by page (a scanned PDF has none and says so) |
| Word (`.docx`) | Markdown: headings, lists, tables |
| Excel (`.xlsx`) | One Markdown table per sheet (first 500 rows of each) |
| PowerPoint (`.pptx`) | One section per slide: title, text, tables, speaker notes |
| Images, legacy `.doc`/`.xls`/`.ppt`, archives, media | A note saying the content can't be read as text |

## Documents agents write

`create_document` writes a finished file into the task's workspace. The path's extension picks the
format:

| Format | Built from |
|---|---|
| Word `.docx` | `content` as Markdown: real heading styles, bullet and numbered lists, tables, quotes, code |
| PDF `.pdf` | `content` as Markdown: A4, wrapped text, tables with borders, code, page numbers |
| Excel `.xlsx` | `sheets` (name + rows, first row the header): numbers stored as numbers, bold frozen header, sized columns |
| PowerPoint `.pptx` | `slides` (title + bullets): 16:9, optional title slide, text sized to fit, long slides continued |
| CSV `.csv` | the first sheet |
| Markdown `.md` | `content` |

Without `sheets` or `slides`, they're derived from `content`: its Markdown tables become sheets,
and its `#`/`##` sections become slides. Every file is recorded as an artifact, so it shows in the
chat, the Result tab and the zip download.

PDFs in plain English use the PDF standard fonts and embed nothing. Text with accents or other
scripts (Greek, Cyrillic and so on) uses the DejaVu fonts, embedded and subset. The API image
installs them (`fonts-dejavu-core`); elsewhere, set `PDF_FONT_DIR`. Without them, accents are
dropped (é → e), and characters no font has (emoji, for one) are written as `?`. Code blocks always
use Courier.

## Previews

Files open in place, from the chat, the Result tab, and a workspace's Files panel:

| File | Shown as |
|---|---|
| Markdown, Word | A rendered document (Markdown also has a Source view) |
| Code | Line-numbered source |
| CSV, Excel | A spreadsheet grid, one tab per sheet |
| PowerPoint | Slides |
| PDF | The browser's PDF viewer |
| Images | The image |
| HTML | The page, in a sandbox that runs no scripts and can't reach the app (Source view too) |
| Anything else | A download button |

`GET /api/tasks/{id}/artifacts/{artifactId}/preview` (or
`/api/workspaces/{id}/files/{artifactId}/preview`) returns a file ready to show. Its `kind` is
one of markdown, code, text, table, slides, pdf, image, html or binary. Long files preview their
beginning, and files over 50 MB are download-only. Downloads (`.../content`) carry the file's real
content type.

## Connecting MCP servers

A task can use tools from MCP servers (or any HTTP API) that you connect for it:

- **Before it starts:** in the home composer, **Options → MCP servers**: a name, the server's URL,
  and a bearer token if it needs one. They're connected, and their tools discovered, before the task
  exists. A server that can't be reached is reported at once and leaves no task behind.
- **While it runs, or after:** the **Tools** button in the task's header opens its connections. Add
  servers (any installed tool plugin: MCP over HTTP, `http-api`; stdio MCP when the server allows
  it), switch their tools on and off, refresh, or remove them.

The root agent and every agent it starts can use the enabled tools from their next step. Tools are
named `{connection}__{tool}`, for example `github__search_issues`. Tokens and other secrets are
encrypted in the vault under the task, sent only to the plugin when a tool is called, and never
shown to agents or returned by the API. Connections belong to their task; another task, or another
organization, can't use them. Unlike a workspace's connections, a task's run without approval
rules: connect only servers whose tools you're happy for the task's agents to call.

## Shared memory from files

Files can also become knowledge that every agent in the organization can search. See
[memory.md](memory.md#knowledge-from-files).

## API

| Method | Path | Role | What it does |
|---|---|---|---|
| `POST` | `/api/uploads` | Member | Stage files (multipart `files`) for a new task; returns `upload_id`s |
| `POST` | `/api/tasks` | Member | `attachments`: upload ids the task starts with |
| `POST` | `/api/tasks/{id}/messages` | Member | A follow-up: `{text, attachments?, budget?}`; `202` |
| `POST` | `/api/tasks/{id}/continue` | Member | Continue a stopped task: `{budget?, note?}`; `202`, `409` while running |
| `POST` | `/api/tasks/{id}/attachments` | Member | Attach files (multipart `files`) to an existing task; returns artifact ids |
| `GET` | `/api/tasks/{id}/chat` | Viewer | The conversation: goal, follow-ups and reports, with `files` and `remaining_work` |
| `GET` | `/api/tasks/{id}/artifacts/{artifactId}/preview` | Viewer | A file ready to show |
| `POST` | `/api/memory/files` | Member | Add files to shared memory (`?workspace=` for one workspace's) |
| `GET` / `POST` | `/api/tasks/{id}/connections` | Viewer / Member | The task's tool connections; add `{plugin_id, name, settings, secrets}` |
| `PATCH` / `DELETE` | `/api/tasks/{id}/connections/{cid}` | Member | Switch tools on and off (`{enabled_tools}`), or remove it |
| `POST` | `/api/tasks` | Member | `connections`: tool connections to make before the task starts |

Events: `TaskFollowUp` (a follow-up, with its text and attachments) and `TaskReopened` (a
finished root took one up). Both show in the Activity stream.

## Limits

- **Agents can't see images.** Image attachments are stored, listed and previewed, but models only
  receive text.
- **Legacy Office formats** (`.doc`, `.xls`, `.ppt`) are stored but not read.
- **Each agent can start at most `MaxChildrenPerAgent` agents (default 10) over its whole life**,
  finished ones included. After several rounds of follow-ups, the root may have to do more of the
  work itself.
- **A finished task can't be moved to another model**; follow-ups use the model it ran on. Fork it
  to change models ([llm-settings.md](llm-settings.md)).
- **Generated PowerPoint decks are plain**: titles and bullets, no images, charts or speaker notes.
