# Memory

Agents remember through three tools, backed by `IMemoryStore`:

| Tool | What it does |
|---|---|
| `write_memory` | Saves a key and value. `shared: true` makes it **shared knowledge**; otherwise it's the agent's own working memory. In a workspace, shared knowledge stays in the workspace unless `organization_wide: true`. |
| `read_memory` | Reads a key: the agent's own entry, or a shared one it can see. |
| `search_knowledge` | Searches the shared knowledge it can see, best matches first. |

## Organization and workspace knowledge

Shared knowledge belongs either to the whole organization or to one workspace:

| Who | Finds |
|---|---|
| A task's agents | The organization's knowledge |
| A workspace's agents | The organization's knowledge, plus their workspace's own |
| Another workspace's agents | Never this workspace's knowledge |

"A workspace's agents" means every agent of its pipeline runs: the stage agents and the helpers
they start. Each run is a task with its own id (`run-…`), so the runtime doesn't go by the task id:
it stamps the agent's workspace on every tool call (`ToolExecutionRequest.WorkspaceId`), and
`write_memory`, `read_memory`, `search_knowledge` and the skill tools scope by that. What a stage
saves with `shared: true` is therefore found by later runs of the same workspace and by no other
workspace; only `organization_wide: true` shares it with everyone.

> Before this was fixed (October 2026), stage agents' shared entries were saved organization-wide.
> Such entries show on the organization's **Shared memory** page with a stage agent's id
> (`stg-…`) as their author; delete them there, or add them again to the workspace's knowledge.

People add workspace knowledge from **Shared memory** with the **Scope** picker set to the
workspace (or the workspace's **Skills & knowledge** tab), as text or files. Over the API, add
`?workspace={id}` to `GET /api/memory`, `POST /api/memory` and `POST /api/memory/files`. A workspace
of another organization answers `404`. Search filters by workspace in the same query as the tenant,
so another workspace's entries are never candidates.

## Hybrid search

With an embedding provider configured, every entry is embedded when it's written, and stored with
**pgvector** in Postgres. A search scores each candidate on three things and ranks by the sum:

| Signal | How | Weight (`Memory:Search`) |
|---|---|---|
| Meaning | Cosine similarity between the query's and the entry's embeddings | `VectorWeight` 0.6 |
| Words | Postgres full-text rank (English stemming), boosted for a literal substring match | `KeywordWeight` 0.3 |
| Recency | Halves every `RecencyHalfLifeDays` (30) | `RecencyWeight` 0.1 |

- An entry must match by meaning (similarity ≥ `MinSimilarity`, 0.35) or by words to be returned at
  all; an empty query lists the most recent entries.
- Vectors are only compared with vectors from the **same model** (`EmbeddingModel` is stored with
  each entry), so switching models never mixes vector spaces. Entries written under an older model
  are still found by words until they're written again.
- Rewriting an entry re-embeds it.

**Without embeddings it still works.** With `Memory:Embeddings:Provider=None` (the default), or a
Postgres without the pgvector extension, search uses words and recency alone. If the embedding
provider is down when an entry is written, the entry is saved anyway and found by words.

**Isolation.** Every query filters by organization before anything is scored, so another
tenant's entries are never candidates, however similar they are. Working memory stays scoped to
the agent that wrote it.

## Configuration

| Setting (env) | |
|---|---|
| `Memory:Embeddings:Provider` (`EMBEDDING_PROVIDER`) | `None`, `Ollama` or `OpenAI` |
| `Memory:Embeddings:Model` (`EMBEDDING_MODEL`) | Default `nomic-embed-text` (Ollama) or `text-embedding-3-small` (OpenAI) |
| `Memory:Embeddings:BaseUrl` (`EMBEDDING_BASE_URL`) | Blank: Ollama on the Docker host, or api.openai.com |
| `Memory:Embeddings:ApiKey` (`EMBEDDING_API_KEY`) | OpenAI only; blank reuses `LLM_API_KEY` when the LLM provider is OpenAI |
| `Memory:Search:*` | The weights, half-life, minimum similarity and result cap above |

For local, free embeddings: `ollama pull nomic-embed-text`, then set `EMBEDDING_PROVIDER=Ollama`.

The Compose file runs `pgvector/pgvector:pg16`, which is Postgres 16 with the extension, so
existing data volumes keep working. The migration enables the extension and adds the `Embedding`
column only where the extension is available.

## Not done yet

**Consolidation**, meaning merging near-duplicate entries and summarizing old ones in the
background, is a planned follow-up.

## Tests

`MemorySearchTests` run against a real Postgres with pgvector, using a deterministic embedding
model:
- a query with no words in common with the entry still finds it by meaning;
- the keyword path works with no embedding provider;
- another organization's entries are never returned;
- equally relevant entries rank newest first;
- rewriting an entry re-embeds it.

`WorkspaceScopeTests` (unit, through the agents' tools) and `WorkspaceScopeApiTests` (API and
Postgres) check that a workspace's knowledge is found by its agents and by no task or other
workspace, that `organization_wide` shares it with everyone, and that `?workspace=` keeps the
organization's and the workspace's apart. `PipelineTests` checks it end to end: a stage shares a
fact during a run, the workspace's next run finds it, and another workspace's run doesn't.

## Knowledge from files

Shared memory → **Add knowledge → From files** (or `POST /api/memory/files`, multipart `files`)
turns documents into knowledge. Supported types are PDF, Word, Excel, PowerPoint, CSV, Markdown,
text and code. Each file's text is split at paragraph breaks into passages of up to about 4,000
characters. Each passage becomes a shared entry, keyed by the file name (`report.pdf (part 2 of 5)`),
so a search finds the passage that matters. Adding a file with the same name again replaces its
passages. Files with no readable text (images, scanned PDFs) are reported back and not added.
