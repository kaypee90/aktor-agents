# Memory

Agents remember through three tools, backed by `IMemoryStore`:

| Tool | What it does |
|---|---|
| `write_memory` | Saves a key and value. `shared: true` makes it **shared knowledge** for every agent of the organization; otherwise it's the agent's own working memory. |
| `read_memory` | Reads a key: the agent's own entry, or a shared one. |
| `search_knowledge` | Searches shared knowledge, best matches first. |

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
