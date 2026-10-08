# Studies walkthrough: will an 8% rent increase cost us renewals?

A hands-on test of **Studies** ([studies.md](studies.md)) with sample data in
[`samples/studies/lease-renewals/`](../samples/studies/lease-renewals). It exercises every tab:
datasets with a sealed holdout, a document, a fact, models with reviews and holdout scores, a
simulated experiment with calibration, the evidence behind it all, and the report.

The data has **known effects built in** (see `generate.py`), so you can check what the agents find
against the truth. Each section below says what a correct analysis should show.

## The question

> **What happens to lease renewals if we raise rents 8% at renewal next year, compared with this
> year's average of about 4.5%? Which tenants are most at risk, and what will occupancy look like
> over the next six months?**

## The sample files

| File | What it is | Add it as |
|---|---|---|
| `lease-renewals.csv` | 600 leases that ended 2022–2025: building, unit type, rent, the rent increase offered, tenure, household income, maintenance requests, and whether the tenant renewed (`Yes`/`No`). 15 rows have no income. | Dataset |
| `occupancy.csv` | 48 months of building occupancy (%), 2022–2025, with a summer peak. | Dataset, with **Month** as the time column |
| `regional-rental-market-2026.md` | A market report: competitors' average increase (4.5%), a $500 move-in credit, new supply near Riverside, and a metro renewal rate of 55–58%. | Document |
| A typed fact | Your company's rule on increases (below). | Fact |

**The truth in the data** (computed on all 600 rows with the same libraries the sandbox uses):

| Effect | Built in | What the data shows |
|---|---|---|
| Overall renewal rate | | **63%** renew (37% leave) |
| Rent increase | each point lowers the odds | odds ratio **≈ 0.73 per point**: 82% renew at ≤ 3%, 60% at 3–6%, 46% above 6% |
| Tenure | longer stays renew more | odds ratio **≈ 1.24 per year** |
| Maintenance requests | each one lowers renewal | odds ratio **≈ 0.86 per request** |
| Building | Riverside renews least | Riverside 59%, Harbor View 62%, Elm Court 67% |
| Unit type | 2- and 3-bedroom renew more | odds ratios ≈ 1.9 and 1.4 against 1-bedroom |
| Income | a small effect | ≈ 1.01 per $1k |
| How predictable renewal is | | cross-validated AUC **≈ 0.74** with all of these; ≈ 0.68 with the increase alone |
| An 8% increase | | the fitted model predicts **≈ 64% → ≈ 40%** renewing (4.5% vs. 8% for everyone) |
| Occupancy | slow upward trend, summer peak | mean 92.4%, between about 90% and 95% |

The report deliberately **disagrees** with your data on one number: it says the metro renewal rate
is 55–58%, while your leases renewed at 63%. A good study notices and says which it relies on.

## Before you start

- **The analysis sandbox:** `docker compose up` builds it; running locally, build it once with
  `docker build -t aktor-analysis:1 docker/analysis`.
- **A model:** with a real model (Settings → AI model) the agents plan the analysis themselves. With
  the **Mock** demo model a study still runs end to end on the same real statistics, but the plan is
  scripted (see [With the Mock model](#with-the-mock-model)).
- Regenerate the files any time with `python3 samples/studies/lease-renewals/generate.py` (the
  output is always the same).

## 1. Create the study

**Studies → New study**:

- **Name:** `Lease renewals 2026`
- **Question:** paste the question above.
- **Model:** pick one of your models, or leave the organization's default. The study's runs and its
  simulated participants use it; you can change it later with **Edit**, or pick another for one run.

## 2. Add the datasets

**Sources → Add dataset**, and choose both CSV files (or drop them on the box).

Open **lease_renewals** (click its name) and check the profile:

- The column names are cleaned for SQL: `Rent Increase %` → `rent_increase_pct`,
  `Household Income ($k)` → `household_income_k`, `Maintenance Requests (12m)` →
  `maintenance_requests_12m`. The original name stays under each.
- `household_income_k` shows **15 missing**; `renewed` is categorical with values `Yes` and `No`.
- **480 rows** are usable and **120 are sealed** as holdout (20%, a random sample).

Write the **data dictionary** (it goes into every agent's prompt) and **Save**:

| Column | Meaning |
|---|---|
| `renewed` | Yes = the tenant signed a renewal; No = they moved out at the end of the lease |
| `rent_increase_pct` | The increase offered at renewal, in percent of the current rent |
| `maintenance_requests_12m` | Maintenance requests the tenant filed in the last 12 months of the lease |
| `tenure_years` | How long the tenant had lived in the unit when the lease ended |

Open **occupancy**, set **Time column** to `month` and **Save**. It's re-split as version 2: the
holdout becomes the **last 10 months** (the most recent ones) instead of a random sample, which is
what you want for a forecast. Its 38 earlier months stay usable.

## 3. Add the document and a fact

- **Sources → Documents → Add documents**: `regional-rental-market-2026.md`.
- **Documents → Manage →** (the study's knowledge page) **Add knowledge → Write**:
  - **Key:** `Rent increase policy`
  - **Knowledge:** `Company policy caps renewal increases at 10%. The board is considering 8% for 2026 and wants the expected loss of renewals before deciding.`

**Overview → Data-use plan** now lists four sources, all *No role yet*:
`dataset:lease_renewals`, `dataset:occupancy`, `document:regional-rental-market-2026.md` and
`document:facts`. The agents must give each one a role before their report is accepted.

## 4. Run it

**Run study**, with these instructions:

```text
Estimate how an 8% increase would change renewals compared with this year's average of about 4.5%,
and which tenants are most at risk. Forecast occupancy for the next 6 months. Simulate tenants
offered 4.5% (control) and 8% (treatment), built from the renewal data and calibrated against the
real renewal rate. Have every model reviewed and score the best one on the holdout.
```

**Overview → Runs → Agents →** opens the live agent graph: the lead, the modelers and reviewer it
starts, their tool calls (`query_dataset`, `fit_model`, `run_analysis`, `run_simulation`, …) and
messages. A real model takes a few minutes; the study page refreshes as results arrive.

## 5. What to look at

### Overview

- **Data-use plan:** every source has a role, e.g. `lease_renewals` → *Model input* (and
  *Population*/*Calibration* if it was used for the simulation), `occupancy` → *Model input*, the
  market report → *Scenario facts*, the policy fact → *Scenario facts*. A source marked *Not
  relevant* has a reason. The evidence count next to each says how often it was actually used.

### Models

Expect at least two models.

- **Renewal (logistic regression, target `renewed`):** check the odds ratios against the table
  above. `rent_increase_pct` should be about **0.73** with a 95% interval that stays below 1;
  `tenure_years` above 1; `maintenance_requests_12m` below 1; Riverside below the other buildings.
  - **Cross-validation** AUC around **0.7–0.75**, and a **sealed holdout** score (green box) in the
    same range. A model that does much better on its training data than on the holdout is
    overfitting.
  - **Status *accepted*** with a reviewer different from the author, and the reviewer's notes.
- **Occupancy (ARIMA on `occupancy_pct`, ordered by `month`):** a 6-month forecast with 95%
  intervals, roughly **90–95%**, and a backtest error of about a point. If the model ignores the
  summer peak, its Ljung-Box warning (autocorrelated residuals) says so.
- **Hypotheses** above the models, recorded *before* the fits, e.g. "Higher rent increases lower the
  chance of renewal."

### Experiments

A simulation such as *rent-increase-8pct*:

- **Population:** segments built from the renewal data (e.g. long-tenure vs. first-year tenants,
  Riverside vs. other buildings) in their real proportions, citing the query that measured them.
- **Bars:** the share choosing *renew* vs. *leave* under **control (4.5%)** and **treatment (8%)**.
- **Effect:** how many more leave under 8%. Compare it with the model: on this data the logistic
  regression predicts renewals falling from about **64% at 4.5% to about 40% at 8%**, roughly
  **24 points** fewer.
- **Calibration:** the simulated control's leave rate against the **real 37%** from the data. A gap
  under 10 points is shown green; a larger one in amber means the simulated tenants don't behave like
  yours, and the report should trust the model over the simulation.
- Every decision is saved as a dataset: **Sources → Simulated datasets**.

### Evidence

Every query, fit, analysis, holdout score, document passage and simulation, newest first. Filter by
kind, and click an id to see the exact SQL or Python and what came back (charts included, if an
agent plotted one). Try the ids cited by a finding in the report: each one should show the numbers
the finding states.

### Report

- **The answer** in the summary: the expected change in renewals at 8%, the tenants most at risk
  (short tenure, many maintenance requests, Riverside), and the occupancy outlook.
- **Findings**, each *Supported* (citing evidence ids and model ids) or *Interpretation*; findings
  resting on the simulation carry **Simulated: directional**.
- **Limitations**, e.g. observational data (the increase wasn't randomly assigned), and the
  conflict between the market report's 55–58% and the data's 63%.
- **Data coverage:** every source, its role and how much evidence came from it.

### Also try

- **Notebook** (study header): a `.ipynb` with every query and model fit. Download the datasets
  (the download icon on each) into a `data/` folder next to it and run it in Jupyter.
- **Analytics → Studies:** the run's spend (agents vs. simulated participants), models by method
  and their reviews, the simulation's calibration gap, and how many findings cite evidence.
- **Run again** with other instructions, e.g. "Compare a 6% increase with a $500 retention credit
  for Riverside tenants." Each run keeps its own report; the newest shows on the Report tab.
- Change the renewal dataset's **Holdout %** to 30 and save: a new version is split, and new models
  use it (older evidence keeps naming the version it used).

## With the Mock model

The Mock model runs the same tools and the same real statistics, on a fixed script:

1. It gives every source a role, records one hypothesis, queries the first dataset and fits one
   model on it. Its column choice is mechanical: it picks the first two-valued *numeric* column as a
   logistic target, else the last numeric one for a linear regression. In `lease_renewals`,
   `renewed` holds text (`Yes`/`No`), so it fits a **linear regression of
   `maintenance_requests_12m`**, not the renewal model a real model would choose.
2. A **Model Reviewer** agent accepts it, and it's scored on the holdout.
3. A 6-participant simulation runs with decisions picked by a hash of each prompt (not reasoning).
4. It submits a short report citing the evidence, and finishes.

So with Mock you can check the mechanics (profiles, the holdout, reviews, evidence ids, the report
checks, analytics), but not the conclusions. The table of true effects above is for a real model.

## If something goes wrong

| What you see | Why, and what to do |
|---|---|
| *"The analysis sandbox isn't available"* | Docker isn't running, or the `aktor-analysis:1` image isn't built (see [Before you start](#before-you-start)). |
| A run ends *Partly done* | It ran out of budget or time before the report was accepted. Its models and evidence are kept; run again, perhaps with narrower instructions. |
| *"The report wasn't accepted"* in the agent graph | The runtime refused it (a source without a role, an invented evidence id, or a model nobody reviewed); the lead fixes it and resubmits. |
| *"Calibration gap"* warning | The simulated tenants' baseline is far from the real rate: trust the model's numbers over the simulation's. |
| *"Low diversity"* warning | Nearly every simulated tenant chose the same; the simulation adds little. |
