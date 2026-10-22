# Idea walkthrough: should we build maintenance triage for small landlords?

A hands-on test of **evaluating an idea** with Studies ([studies.md](studies.md#evaluating-an-idea)),
using the sample knowledge in
[`samples/studies/idea-maintenance-triage/`](../samples/studies/idea-maintenance-triage). It covers
the brief, knowledge as sources, the assumption register, a customer experiment, financial
projections, a what-if, and the verdict.

## The idea

| Field | Paste this |
|---|---|
| **Name** | `Maintenance triage for small landlords` |
| **The idea** | An AI assistant that takes tenants' maintenance requests by text, triages them by urgency and books a vetted local contractor for the landlord to approve. |
| **Who it's for** | Independent landlords with 5 to 50 units who manage their buildings themselves. |
| **The problem it solves** | Requests arrive by text and phone at all hours; landlords sort and dispatch them by hand, about 6 hours a month, and slow repairs cost tenants. |
| **How it makes money** | Subscription at $39 a month per landlord. |
| **Known costs and constraints** | Two engineers for six months, then one; LLM and messaging about $1 per landlord a month. |
| **Horizon / currency** | 36 months, USD |

## The knowledge

| File | What's in it |
|---|---|
| `landlord-market-notes.md` | How many landlords, how they handle maintenance today, a survey of what they'd pay, competitors, ad costs and conversion, typical churn. |
| `build-and-run-costs.md` | Build team and time, running costs per landlord, support and hosting, and constraints (contractor networks per city, licensing, carrier registration). |

The figures are illustrative, not sourced. Read them before the run, so you can check what the
agents make of them.

## 1. Create the study

**Studies → Evaluate an idea**, fill in the brief above, pick a model, and **Create**.

## 2. Add the knowledge

**Sources → Documents → Add files**, and choose both `.md` files. They become searchable passages
the agents cite by evidence id.

## 3. Run it

**Run study** (optionally with instructions, e.g. "Compare $29 and $49 a month"). Open **Agents →**
on the run to watch the team form. With a real model, expect something like:

- the lead reads the sources, gives each a role, and spawns specialists (a market and customer
  researcher, a financial modeler, a feasibility analyst, a skeptic);
- **assumptions** with ranges that cite the documents, for example:
  - `market_size` around 48,000;
  - `adoption_rate` from the survey (about 25% would pay $40 or more, and fewer than that would
    actually switch);
  - `monthly_churn` 3–5%;
  - `cac` from $4–$7 a click at a 2–4% conversion ($100–$350);
  - `fixed_costs` and `upfront_investment` from the cost notes;
  - `launch_month` 5–7;
- an **experiment** in which simulated landlords choose between their shared inbox and the
  assistant at $39;
- a **base-case** projection and at least one **pessimistic** one;
- reviews of the assumptions by an agent that didn't record them;
- a **verdict**.

## 4. Read the business case

**Business case** tab:

- **Assumptions**, by category. Look for *Guess: no evidence* and *Challenged* badges: those are
  the numbers to distrust.
- **Projections**: the chance it pays back within 36 months, median NPV, the funding needed, and
  the cash fan chart (median line, P10–P90 band). The tornado shows which drivers move NPV most.
  In this case it's usually `cac`, `monthly_churn` and `adoption_rate`.
- **Show the drivers…** under a projection lists where each input came from.

## 5. Ask "what if"

On the base case, **What if…**:

1. Set `price` to **Type a range**: `45`, `49`, `55`.
2. **Run projection**.

A new projection, marked *what-if by a person*, appears at the top. Compare its payback chance
and NPV with the base case. Then try `marketing_spend` at the default (0) with `organic_rate` at
`0.02` – `0.03` – `0.05` (word of mouth only), or `launch_month` at 9–12 months.

## 6. Read the verdict

**Overview** shows the verdict: a recommendation, usefulness / feasibility / profitability out of
10, and the lead projection's headline numbers. **Report** adds the risks, the assumptions the
verdict depends on, changes, next steps, and the findings with their evidence.

Things to check:

- Every score cites evidence; profitability cites a projection. The runtime refuses a report
  where profitability scores 7+ while the projections rarely pay back.
- The **assumption check** line counts guesses and unreviewed assumptions.
- Feasibility should mention the constraints in the cost notes (a contractor network per city,
  booking as a referral where a licence is needed, carrier registration).

## With the Mock model

With **Mock** the flow is scripted, but every step runs for real:

1. A knowledge search.
2. Ten placeholder assumptions citing that search, with the price taken from the brief ($39).
3. A small customer experiment (needs the analysis sandbox image to save its decisions).
4. Base-case and pessimistic projections, computed by the runtime.
5. A reviewer agent that accepts the assumptions.
6. A verdict scored from the base case's payback chance.

The projections and the verdict's checks are real; the numbers going in are not.
