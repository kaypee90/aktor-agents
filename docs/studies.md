# Studies

**Studies** (sidebar, under Observe) is where agent teams research a question with your data. You
give a study its sources (datasets, documents, and connections to MCP servers and APIs) and a
question or scenario. The agents explore the data, fit statistical models, run simulated
experiments, review each other's work and write a report in which every finding links to the
evidence behind it.

It replaces the old **Simulation** page. Simulation now lives inside studies as **experiments**:
simulated populations, generated from your data, that react to a scenario and produce a table of
decisions the same statistical tools can analyze.

## The principle: models decide, code computes

LLMs don't do statistics in their heads here. Agents decide *what* to analyze; every number comes
from code the runtime ran in a sandboxed Python container (pandas, statsmodels, scikit-learn,
scipy, sympy, DuckDB), with the data version, the code and the output recorded. Each such result
has an **evidence id** (`ev-…`), and the report's findings cite them.

## A study

| Part | What it is |
|---|---|
| **Question** | What the study should find out, e.g. "What happens to renewals if rents rise 8%?" |
| **Datasets** | CSV, Excel, Parquet or JSON files. Profiled on upload (columns, types, missing values, ranges), versioned, with a data dictionary you can edit. A share of each (20% by default) is **sealed as holdout**: agents never see it, and only `evaluate_on_holdout` scores models on it. Mark a time column and the holdout is the most recent rows instead of a random sample. |
| **Documents** | Reports, papers, policies: the study's own knowledge (searchable passages, cited by evidence id). |
| **Connections** | MCP servers and APIs, set up for this study only (secrets in the vault). Every response agents use is snapshotted as evidence, so results can be checked and reproduced later. |
| **Runs** | Each **Run** starts an agent team on the question. A study can be run again after sources change. |

Everything belongs to one study: another study (or organization) never sees its data, documents,
connections or secrets. Deleting a study deletes all of them.

## Study tools

Every agent of a study run gets these (children inherit them, whatever their role):

| Tool | Does |
|---|---|
| `study_sources` | Lists the study's datasets (profile, dictionary), documents, connections, and the data-use plan. |
| `query_dataset` | Read-only SQL (DuckDB) over the training part of the datasets. |
| `record_hypothesis` | Records a hypothesis before testing it. |
| `fit_model` | Linear regression, logistic regression or ARIMA on the training data, with coefficients, intervals, fit statistics, cross-validation and diagnostics. Registers a candidate model. |
| `run_analysis` | Any other analysis as Python (e.g. calculus with sympy, differential equations with scipy, charts with matplotlib). |
| `review_model` | Accepts or rejects a model with notes. The runtime refuses a review by the model's own author. |
| `evaluate_on_holdout` | Scores a reviewed model on the sealed holdout. Limited per study, so the holdout can't be fished. |
| `set_source_role` | The data-use plan: what each source is for (model input, calibration, population, scenario facts, validation, or not relevant with a reason). |
| `run_simulation` | An experiment: a population built from the data reacts to the scenario under control and treatment conditions, over rounds and repeated runs. The decisions become a dataset. |
| `submit_report` | The study report. The runtime checks it before the run can finish. |

Results of `search_knowledge` and of connection tools also get evidence ids automatically.

## What the runtime enforces

- **Citations are real.** A finding's evidence ids must exist in this study. Findings without
  evidence are kept but shown as *interpretation*, not as results.
- **Every source is accounted for.** The report is refused while any source has no role in the
  data-use plan, and a source marked *not relevant* needs a reason.
- **Reviews.** A model cited in a finding must have been accepted by an agent other than its author.
- **Sealed holdout.** Agents fit and query training data only. Holdout scores come only from
  `evaluate_on_holdout`, at most `Studies:MaxHoldoutEvaluations` times per study (3).
- **Sandbox.** Analysis code runs in a container with no network, the data mounted read-only, and
  memory, CPU and time limits (`Studies:AnalysisMemoryLimit` 1g, `AnalysisCpuLimit` 1,
  `AnalysisTimeoutSeconds` 120).
- **Simulation limits.** Participants (40), rounds (4), conditions (3) and repeated runs (3) are
  capped, and a simulation's cost counts against the study's daily budget.
- **The root can't finish without a report.** `complete_task` is refused until `submit_report`
  succeeded.

## Experiments (simulation)

`run_simulation` takes:
- **Population:** segments with their share and attributes (taken from the data, citing evidence),
  optionally numeric attributes sampled per participant within a range.
- **Conditions:** a control and up to two treatments, each a scenario text.
- **Decision:** the choices a participant picks from, an optional number (e.g. a price they'd
  accept), and always a short reason.
- **Rounds** and **word of mouth:** in later rounds participants hear what a few others chose.
- **Calibration:** optionally, the real rate of one choice under the control condition (e.g. 12%
  leave, from the data). The runtime reports the gap between simulated and real.

The runtime generates the participants (seeded, so a run can be reproduced), asks each for a
structured decision with the organization's fast model, and saves every decision as a row of a new
**simulated dataset** that `query_dataset` and `fit_model` work on. It reports choice shares per
condition and round, the calibration gap, and a **low diversity** warning when nearly everyone
chose the same.

Simulated people are not real people: they are more alike and more agreeable than real
populations. The report always labels simulated findings separately from findings based on real
data, and agents are told to treat them as directional evidence.

## The report

- Summary, findings (each with evidence and a confidence), the models and their holdout scores,
  limitations and open questions.
- A **data coverage** table: every source, its role, and how often it was used.
- **Notebook export** (`.ipynb`): every analysis run's code, to rerun on the datasets.

## Analytics

Analytics has a **Studies** tab: studies and runs, spend and tokens (by stage), analysis runs by
method and their compute time, simulations (participants, decisions, calibration gaps), data
sources (uploads, connection calls), and evidence quality (cited findings, rejected citations,
reviews).

## API

| Method | Path | |
|---|---|---|
| GET | `/api/studies` | The organization's studies. |
| POST | `/api/studies` | `{name, question}`. Member. |
| GET | `/api/studies/{id}` | The study with its sources, plan, runs, models, hypotheses and latest report. |
| PATCH | `/api/studies/{id}` | `{name, question}`. Member. |
| DELETE | `/api/studies/{id}` | Deletes the study and everything in it. Admin. |
| POST | `/api/studies/{id}/datasets` | Multipart `files`: adds datasets (a file with the same name adds a new version). |
| PUT | `/api/studies/{id}/datasets/{datasetId}` | `{dictionary, time_column, holdout_fraction}`; re-splits when the holdout changes. |
| DELETE | `/api/studies/{id}/datasets/{datasetId}` | Removes a dataset. |
| POST | `/api/studies/{id}/runs` | Starts a run; `{instructions}` optional. Returns the task. |
| GET | `/api/studies/{id}/evidence/{evidenceId}` | One piece of evidence: what was run and what came back. |
| GET | `/api/studies/{id}/files/{name}` | A chart an analysis saved. |
| GET | `/api/studies/{id}/notebook` | The notebook export. |

Documents use `/api/memory…?workspace={study.workspace_id}` and connections
`/api/workspaces/{study.workspace_id}/connections`, as for a workspace.

## How it works

A study is a row in `Studies` plus a workspace of kind `study` (hidden from **Workspaces**). The
workspace gives the study its connections, knowledge, safety policy and daily budget: a study
run's root agent is created inside it, and every agent it spawns inherits it. Datasets live under
`{Tools:WorkspaceRoot}/studies/{id}/datasets`, as Parquet split into `train` and `holdout`. The
analysis image (`docker/analysis`) runs one job per call from `runner.py`: ingest, query, fit,
analysis, holdout evaluation.
