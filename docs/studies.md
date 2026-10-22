# Studies

**Studies** (sidebar, under Observe) is where agent teams research a question with your data. You
give a study its sources (datasets, documents, and connections to MCP servers and APIs) and a
question or scenario. The agents explore the data, fit statistical models, run simulated
experiments, review each other's work and write a report in which every finding links to the
evidence behind it.

It replaces the old **Simulation** page. Simulation now lives inside studies as **experiments**:
simulated populations, generated from your data, that react to a scenario and produce a table of
decisions the same statistical tools can analyze.

A study either answers a **question** with data, or **evaluates an idea** (a product, a business, a
change): you describe the idea and give it what you know, and agents judge whether it is useful,
feasible and profitable, simulating customers and finances on the way. See
[Evaluating an idea](#evaluating-an-idea).

## The principle: models decide, code computes

LLMs don't do statistics in their heads here. Agents decide *what* to analyze; every number comes
from code the runtime ran in a sandboxed Python container (pandas, statsmodels, scikit-learn,
scipy, sympy, DuckDB), with the data version, the code and the output recorded. Each such result
has an **evidence id** (`ev-…`), and the report's findings cite them.

To try it with sample data, follow [studies-walkthrough.md](studies-walkthrough.md) (a question) or
[studies-idea-walkthrough.md](studies-idea-walkthrough.md) (an idea).

## A study

| Part | What it is |
|---|---|
| **Question** | What the study should find out, e.g. "What happens to renewals if rents rise 8%?" |
| **Datasets** | CSV, Excel, Parquet or JSON files. Profiled on upload (columns, types, missing values, ranges), versioned, with a data dictionary you can edit. A share of each (20% by default) is **sealed as holdout**: agents never see it, and only `evaluate_on_holdout` scores models on it. Mark a time column and the holdout is the most recent rows instead of a random sample. |
| **Documents** | Reports, papers, policies: the study's own knowledge (searchable passages, cited by evidence id). |
| **Connections** | MCP servers and APIs, set up for this study only (secrets in the vault). Every response agents use is snapshotted as evidence, so results can be checked and reproduced later. |
| **Model** | The provider and model the study's runs use (the organization's default unless chosen): its agents and its simulated participants. A run can use another one without changing the study's. |
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
| `record_assumption` | Records a number the case rests on as a range (low, likely, high) with the evidence it comes from. |
| `review_assumption` | Accepts or challenges another agent's assumption. The runtime refuses a review by its author. |
| `project_financials` | Simulates the business model month by month, thousands of times (see [Financial projections](#financial-projections)). |
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
- **An idea gets a verdict that agrees with its numbers** (see [The verdict](#the-verdict)).

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
structured decision with the run's model (its fast model, if it has one), and saves every decision as a row of a new
**simulated dataset** that `query_dataset` and `fit_model` work on. It reports choice shares per
condition and round, the calibration gap, and a **low diversity** warning when nearly everyone
chose the same.

### Running an experiment yourself

Once no run of the study is in progress, **Run experiment** on the Experiments tab sets one up by
hand, and **Run again with changes** on an experiment fills the form with its population,
conditions, decision, facts and calibration. You choose:

- the **participants**, **rounds**, **repeated runs** (each with new people) and the **seed**
  (same seed, same people), within the same caps as an agent's experiment;
- **word of mouth** (with more than one round);
- the **decision**: its question, options, and an optional number with its range;
- up to three **conditions**, the first being the control;
- the **population**: segments with their share, description, fixed attributes and numeric
  attributes drawn per participant;
- **facts** everyone knows, a **calibration** rate, and the **model** participants decide with.

The experiment runs in the background (one at a time per study) with a progress bar, and is saved
like an agent's: a simulated dataset, evidence, and a card marked *set up by hand* (agent `person`,
no run). Its cost counts against the study's daily budget, and it stops at the same cost limit. A
population an agent built cites its evidence; one you set up from scratch may cite none, and is
recorded as set up by you. If the server restarts or the experiment fails, it shows as failed with
the reason until you dismiss it.

Simulated people are not real people: they are more alike and more agreeable than real
populations. The report always labels simulated findings separately from findings based on real
data, and agents are told to treat them as directional evidence.

## Evaluating an idea

**Studies → Evaluate an idea** creates an idea study from a brief:

| Field | What to write |
|---|---|
| **The idea** | What it is and what it does. |
| **Who it's for** | The customers. |
| **The problem it solves** | And what people do about it today: the alternative they'd switch from. |
| **How it makes money** | Prices, plans, who pays. |
| **Known costs and constraints** | Team, build time, running costs, budget, rules. |
| **Horizon and currency** | How far ahead projections look (6–120 months), and the money they're in. |

Then give it **knowledge** as sources, as for any study: market reports, competitor notes, cost
quotes, survey results (documents), sales or usage data (datasets), APIs (connections). An idea
can run on its brief alone, but then most of its numbers are guesses, and the report says so.

A run judges the idea on three things:

- **Usefulness:** do the customers have the problem, would they switch from today's alternative,
  and what would they pay? Evidence: documents, data, and experiments (`run_simulation`) in which
  simulated customers choose between their current way and the idea.
- **Feasibility:** can it be built and run (technology, skills, time, cost, rules, competition)?
- **Profitability:** does it make money within the horizon? This one is computed, not estimated.

Agents organize themselves as for any study; the lead is told that a market and customer
researcher, a financial modeler, a feasibility analyst and a skeptic who reviews the assumptions
often pay off. Numbers in the brief are treated as claims to check, not facts.

### Assumptions

Every number the case rests on is an **assumption** (`record_assumption`): a name (a driver name,
like `monthly_churn`, when it feeds the business model), a category (market, customer, pricing,
cost, operations, technical, regulatory, competition), a unit, a **range** (low, likely, high), the
**evidence ids** it comes from, and a rationale. Without evidence it's shown as a **guess**.
Recording the same name again replaces it (the old one stays for the projections that used it).
Another agent reviews each one (`review_assumption`: accept, or challenge with a suggested range).

The **Business case** tab lists the register by category, with each assumption's evidence and
review.

### Financial projections

`project_financials` runs a business model in the runtime (`BusinessModelSimulator`), not in the
LLM. Its inputs are **drivers**, each an assumption id (preferred), a number, or a range:

| Driver | Meaning | Default |
|---|---|---|
| `market_size` | Potential customers you could reach | required |
| `adoption_rate` | Share of them that would ever buy at this price (e.g. from an experiment) | 1 |
| `initial_customers` | Paying customers at launch | 0 |
| `price` | Revenue per purchase (per month, for a subscription) | required |
| `purchases_per_month` | Purchases per active customer per month | 1 |
| `monthly_churn` | Share of customers lost each month | 0.03 |
| `marketing_spend` | Paid acquisition per month, from launch | 0 |
| `cac` | Cost to acquire one paying customer | required with marketing |
| `organic_rate` | New customers per active customer per month (referrals) | 0 |
| `variable_cost` | Cost of each purchase | 0 |
| `fixed_costs` | Fixed costs per month, from month 1 | 0 |
| `upfront_investment` | One-time cost before launch | 0 |
| `launch_month` | Months of building before the first sale | 0 |
| `discount_rate` | Annual rate for NPV | 0.12 |

The **revenue model** is `subscription` (customers pay monthly until they leave), `one_time` (each
buys once) or `usage` (active customers buy `purchases_per_month` times a month).

Month by month: last month's customers bring referrals, some leave, paid marketing adds
`marketing_spend / cac`, and nobody beyond `market_size × adoption_rate` ever buys. Revenue is
customers × purchases × price; profit subtracts variable costs, marketing and fixed costs. The model
runs this 2,000 times (seeded, so it can be reproduced), each time drawing every uncertain driver
from its range (a triangular distribution). It reports:

- the chance the idea **pays back** within the horizon, the median payback month, and how often it
  never does;
- **NPV**, final cash, the **funding needed** (how deep cash goes, the upfront cost included) and
  customers at the end, each as P10 / median / P90;
- revenue and profit **by year**, and monthly series for the charts;
- **unit economics** at the likely values: gross margin, customer lifetime, LTV, CAC, LTV/CAC, CAC
  payback, break-even customers;
- **sensitivity**: NPV with each uncertain driver at its low and high (a tornado chart), so you see
  which assumption to test first;
- warnings (negative margin, LTV below CAC, growth capped by the market, no ranges, drivers
  without evidence), and where each driver came from.

Each projection is evidence (kind `projection`). On the **Business case** tab, **What if…** runs
the same model by hand: start from a projection, change any driver (pick another assumption, type
a range, or use the default), and the result is saved as a projection marked *what-if by a person*.

### The verdict

An idea study's report must include a **verdict**:

- a **recommendation**: `go`, `go_with_changes`, `no_go` or `needs_more_evidence`;
- **usefulness**, **feasibility** and **profitability**, each a score from 0 to 10 with a summary
  and the evidence ids it rests on;
- optionally the **key assumptions** (assumption ids), **risks** (with severity and mitigation),
  **changes** to make, and **next steps** (the cheapest ways to test the riskiest assumptions).

The runtime refuses the report when:
- a score cites no evidence, or an id that doesn't exist;
- profitability cites no projection;
- profitability scores 7 or more while every cited projection pays back in under 40% of simulated
  futures.

It warns (and the report shows it) when profitability scores 3 or less though the projections pay
back in 80% or more; when usefulness rests on no customer evidence; when the recommendation
disagrees with the scores; when key assumptions are guesses or were challenged; when a cited
projection rests on drivers without evidence; and when no assumption was reviewed.

It adds the overall score (the mean of the three), the headline numbers of the first projection
cited (the lead case), and an **assumption check**: how many assumptions cite evidence and were
accepted, challenged or not reviewed, and which are guesses. Findings that rest on a projection are
labelled *Projection: depends on its assumptions*.

The study's **Overview** shows the verdict and the brief; the **Report** adds risks, the
assumptions the verdict depends on, changes and next steps.

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
| POST | `/api/studies` | `{name, question, model, kind, idea}`: `kind` is `question` (default) or `idea`, with `idea` the brief `{idea, customers, problem, revenue_model, costs, horizon_months, currency}`. `model` is a model profile id; left out, the organization's default. Member. |
| GET | `/api/studies/{id}` | The study with its `kind` and `idea`, sources, plan, runs, models, hypotheses, experiments (and those in progress, `pending_experiments`), `experiment_limits`, `assumptions`, `projections` and latest report. |
| PATCH | `/api/studies/{id}` | `{name, question, model, idea}` (`""` for the organization's default; `idea` only on an idea study). Member. |
| DELETE | `/api/studies/{id}` | Deletes the study and everything in it. Admin. |
| POST | `/api/studies/{id}/datasets` | Multipart `files`: adds datasets (a file with the same name adds a new version). |
| PUT | `/api/studies/{id}/datasets/{datasetId}` | `{dictionary, time_column, holdout_fraction}`; re-splits when the holdout changes. |
| DELETE | `/api/studies/{id}/datasets/{datasetId}` | Removes a dataset. |
| POST | `/api/studies/{id}/runs` | Starts a run; `{instructions, model}` optional (model: for this run only). Returns the task. |
| POST | `/api/studies/{id}/experiments` | `{spec, model}`: starts an experiment by hand (`spec` as `run_simulation`'s arguments; population evidence optional). 409 while a run or another experiment is in progress. Member. |
| DELETE | `/api/studies/{id}/experiments/{experimentId}` | Dismisses a failed experiment. Member. |
| POST | `/api/studies/{id}/projections` | A what-if: `project_financials`' arguments `{name, revenue_model, horizon_months, drivers}`. Runs at once. Member. |
| GET | `/api/studies/projection-drivers` | The business model's drivers and revenue models, for the what-if form. |
| GET | `/api/studies/{id}/evidence/{evidenceId}` | One piece of evidence: what was run and what came back. |
| GET | `/api/studies/{id}/files/{name}` | A chart an analysis saved. |
| GET | `/api/studies/{id}/notebook` | The notebook export. |

Documents use `/api/memory…?workspace={study.workspace_id}` and connections
`/api/workspaces/{study.workspace_id}/connections`, as for a workspace.

## How it works

A study is a row in `Studies` (with its `Kind` and, for an idea, `IdeaJson`) plus a workspace of
kind `study` (hidden from **Workspaces**). Assumptions and projections live in `StudyAssumptions`
and `StudyProjections`. The
workspace gives the study its connections, knowledge, safety policy and daily budget: a study
run's root agent is created inside it, and every agent it spawns inherits it. Datasets live under
`{Tools:WorkspaceRoot}/studies/{id}/datasets`, as Parquet split into `train` and `holdout`. The
analysis image (`docker/analysis`) runs one job per call from `runner.py`: ingest, query, fit,
analysis, holdout evaluation.
