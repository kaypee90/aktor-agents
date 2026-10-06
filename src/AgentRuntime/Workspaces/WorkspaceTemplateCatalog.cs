using AgentRuntime.Contracts;
using AgentRuntime.Pipelines;
using AgentRuntime.Safety;

namespace AgentRuntime.Workspaces;

/// <summary>
/// Real-world starting points beside incident response. Each is an ordinary pipeline: change any
/// stage, add or remove stages, and connect your own services. Instructions say what to produce
/// and to what standard; how is left to each stage's agent.
/// </summary>
public static partial class WorkspaceTemplates
{
    private static PipelineStage Stage(string id, string name, string role, string instructions, string[]? inputs = null,
        string[]? capabilities = null, int helpers = 0, StageFailurePolicy onFailure = StageFailurePolicy.FailRun, int retries = 1) => new()
    {
        StageId = id,
        Name = name,
        Role = role,
        Instructions = instructions,
        Inputs = [.. inputs ?? []],
        Capabilities = [.. capabilities ?? ["research"]],
        MaxHelpers = helpers,
        OnFailure = onFailure,
        Retries = retries
    };

    /// <summary>Research and writing work: nothing reaches outside the platform, so nothing needs approval.</summary>
    private static WorkspaceSafetyPolicy ResearchSafety => new()
    {
        Autonomy = AutonomyLevel.Autonomous,
        Rules = [new ApprovalRule { Name = "No shell", ToolPattern = "shell_exec", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Deny }]
    };

    /// <summary>Work that may write to connected systems (a helpdesk, a CRM, a repository): writes wait for a person.</summary>
    private static WorkspaceSafetyPolicy ReviewedWrites => new()
    {
        Autonomy = AutonomyLevel.SemiAutonomous,
        ApprovalTimeoutHours = 24,
        Rules = [new ApprovalRule { Name = "No shell", ToolPattern = "shell_exec", Applies = SideEffectScope.Any, Decision = PolicyDecisionKind.Deny }]
    };

    /// <summary>
    /// Customer support: each new ticket (from the helpdesk's webhook) is classified, matched to the
    /// help centre and past answers, answered in a draft, and checked before anyone sends it.
    /// </summary>
    public static readonly WorkspaceTemplate SupportTriage = new()
    {
        Id = "support-triage",
        Name = "Support ticket triage",
        Category = "Support",
        Description = "Each new ticket is classified by urgency and topic, matched to your help centre, answered in a draft reply, " +
                      "and quality-checked. Urgent tickets reach you at once.",
        Goal = "Triage incoming customer support tickets and draft accurate, on-brand replies for an agent to send.",
        Pipeline = new PipelineDefinition
        {
            MaxRunMinutes = 15,
            MaxConcurrentRuns = 5,
            ResultUrgency = "warning",
            Stages =
            [
                Stage("classify", "Classify", "Support triager",
                    "Read the ticket in the run's input. State its topic, urgency (urgent: outage, payment failure, security or legal threat, " +
                    "an angry customer about to churn; high; normal; low), the customer's sentiment, and what they actually need. " +
                    "Begin your report with URGENT if it is urgent."),
                Stage("research", "Find the answer", "Support researcher",
                    "Find what answers this ticket: search the workspace's knowledge (help centre articles, past answers, policies) " +
                    "and connected services. Quote the passages you rely on. If nothing covers it, say what's missing.",
                    ["classify"]),
                Stage("draft", "Draft reply", "Support writer",
                    "Write the reply to send the customer: warm, plain, specific, in their language, at most 150 words, with clear next " +
                    "steps. Use only facts the research found; never promise refunds, dates or features it didn't confirm.",
                    ["classify", "research"]),
                Stage("check", "Quality check", "Support QA reviewer",
                    "Check the draft against the research and the ticket: correct, complete, polite, no invented facts or promises. " +
                    "Fix what's wrong and report the final reply, the urgency and topic, and whether a person must look at it first.",
                    ["draft"], retries: 0)
            ]
        },
        Safety = ReviewedWrites,
        Webhooks =
        [
            new TemplateWebhook("New tickets", "Triage this support ticket and draft a reply.",
                """{"ticket_id":"T-48213","channel":"email","customer":{"name":"Priya Natarajan","plan":"Business","since":"2023-04"},"subject":"Charged twice this month","body":"Hi, I was charged twice for my March invoice (two payments of $249 on the 3rd). Can you refund one? I need this sorted before our finance close on Friday."}""")
        ],
        DailyTokenLimit = 1_500_000,
        DailyCostLimitUsd = 6m
    };

    /// <summary>Market research: a brief splits into parallel research tracks that a writer combines and a checker verifies.</summary>
    public static readonly WorkspaceTemplate MarketResearch = new()
    {
        Id = "market-research",
        Name = "Market research report",
        Category = "Research",
        Description = "Give it a market or product idea. It researches market size, competitors and customers in parallel, writes a " +
                      "sourced report with a recommendation, and fact-checks it.",
        Goal = "Research a market or product idea and produce a sourced, decision-ready report.",
        SampleInput = "AI scheduling assistants for independent dental clinics in the US.",
        Pipeline = new PipelineDefinition
        {
            MaxRunMinutes = 60,
            Stages =
            [
                Stage("brief", "Brief", "Research lead",
                    "Turn the run's input into a research brief: the market's boundaries, the questions that decide whether it's worth " +
                    "entering, and what each research track should answer."),
                Stage("market", "Market size", "Market analyst",
                    "Estimate the market's size and growth (top-down and bottom-up), with sources and stated assumptions.",
                    ["brief"], helpers: 1, onFailure: StageFailurePolicy.Continue),
                Stage("competitors", "Competitors", "Competitive analyst",
                    "Map the main competitors: positioning, pricing, strengths, weaknesses and funding, in a comparison table with sources.",
                    ["brief"], helpers: 2, onFailure: StageFailurePolicy.Continue),
                Stage("customers", "Customers", "Customer researcher",
                    "Describe the target customers: segments, jobs to be done, pains, buying process and willingness to pay, with evidence.",
                    ["brief"], onFailure: StageFailurePolicy.Continue),
                Stage("report", "Write report", "Report writer",
                    "Combine the research into report.md: executive summary, market, competitors, customers, risks, and a clear " +
                    "go / no-go recommendation with reasons. Cite sources inline.",
                    ["market", "competitors", "customers"], ["research", "filesystem"]),
                Stage("fact-check", "Fact check", "Fact checker",
                    "Check every number and claim in report.md against its source; correct or flag what doesn't hold up, and report what changed.",
                    ["report"], ["research", "filesystem"], retries: 0)
            ]
        },
        Safety = ResearchSafety,
        DailyTokenLimit = 3_000_000,
        DailyCostLimitUsd = 12m
    };

    /// <summary>Code review: a pull request (from a repository's webhook) is reviewed for correctness, security and tests in parallel.</summary>
    public static readonly WorkspaceTemplate PullRequestReview = new()
    {
        Id = "pr-review",
        Name = "Pull request review",
        Category = "Engineering",
        Description = "Each opened pull request is reviewed for correctness, security, and tests and docs at the same time, then " +
                      "combined into one prioritised review. Connect your repository to fetch diffs and post comments.",
        Goal = "Review pull requests thoroughly and consistently before a human reviewer looks at them.",
        Pipeline = new PipelineDefinition
        {
            MaxRunMinutes = 30,
            MaxConcurrentRuns = 3,
            Stages =
            [
                Stage("understand", "Understand the change", "Change summariser",
                    "From the pull request in the run's input (fetch its diff with a connected repository tool if only a link is given), " +
                    "summarise what changes, why, and which areas of the code it touches.",
                    capabilities: ["research", "http"]),
                Stage("correctness", "Correctness", "Correctness reviewer",
                    "Review the change for bugs: logic errors, edge cases, error handling, concurrency, performance regressions. " +
                    "Cite file and line for each finding, with a suggested fix.",
                    ["understand"], ["research", "http"], onFailure: StageFailurePolicy.Continue),
                Stage("security", "Security", "Security reviewer",
                    "Review the change for security issues: injection, authentication and authorisation, secrets, unsafe input, dependency " +
                    "risks. Rate each finding by severity.",
                    ["understand"], ["research", "http"], onFailure: StageFailurePolicy.Continue),
                Stage("tests", "Tests & docs", "Test reviewer",
                    "Check that the change is tested (what's missing, which cases) and documented where users or other developers need it.",
                    ["understand"], ["research", "http"], onFailure: StageFailurePolicy.Continue),
                Stage("review", "Combined review", "Lead reviewer",
                    "Merge the findings into one review: must-fix, should-fix and nits, without duplicates, each with file, line and fix. " +
                    "End with approve, approve with changes, or request changes.",
                    ["correctness", "security", "tests"], ["research", "filesystem"], retries: 0)
            ]
        },
        Safety = ReviewedWrites,
        Webhooks =
        [
            new TemplateWebhook("Pull requests", "Review this pull request.",
                """{"action":"opened","number":482,"title":"Add rate limiting to the public API","author":"dkumar","url":"https://github.com/acme/api/pull/482","body":"Adds a token-bucket limiter (100 req/min per API key) to /v1/*. Limits are kept in memory.","diff":"--- a/src/middleware.ts\n+++ b/src/middleware.ts\n@@\n+const buckets = new Map<string, { tokens: number; at: number }>();\n+export function rateLimit(req, res, next) {\n+  const key = req.headers['x-api-key'];\n+  const b = buckets.get(key) ?? { tokens: 100, at: Date.now() };\n+  b.tokens = Math.min(100, b.tokens + (Date.now() - b.at) / 600);\n+  if (b.tokens < 1) return res.status(429).end();\n+  b.tokens -= 1; buckets.set(key, b); next();\n+}"}""")
        ],
        DailyTokenLimit = 2_000_000,
        DailyCostLimitUsd = 8m
    };

    /// <summary>Sales: each new lead is researched (company and person in parallel), qualified, and given a personal first email.</summary>
    public static readonly WorkspaceTemplate LeadResearch = new()
    {
        Id = "lead-research",
        Name = "Lead research & outreach",
        Category = "Sales",
        Description = "Each new lead from your CRM or forms is researched (company and contact in parallel), scored against your " +
                      "ideal customer profile, and given a personalised first email to review.",
        Goal = "Research inbound leads, qualify them, and draft personalised outreach.",
        Pipeline = new PipelineDefinition
        {
            MaxRunMinutes = 20,
            MaxConcurrentRuns = 4,
            Stages =
            [
                Stage("company", "Company research", "Account researcher",
                    "Research the lead's company: what it does, size, funding, recent news, tech stack and likely priorities, with sources."),
                Stage("contact", "Contact research", "Contact researcher",
                    "Research the contact: role, responsibilities, recent public activity, and what they're likely measured on. Use only " +
                    "public, professional information."),
                Stage("qualify", "Qualify", "Sales qualifier",
                    "Score the lead 1–5 against the ideal customer profile in the workspace's knowledge (or a sensible B2B default), with " +
                    "the reasons, the best angle, and likely objections.",
                    ["company", "contact"]),
                Stage("email", "Draft email", "Outreach writer",
                    "If the score is 3 or more, write a first email: under 120 words, specific to them, one clear ask, no clichés. " +
                    "Otherwise say why to skip. Report the score, angle and email.",
                    ["qualify"], retries: 0)
            ]
        },
        Safety = ReviewedWrites,
        Webhooks =
        [
            new TemplateWebhook("New leads", "Research and qualify this lead, and draft the first email.",
                """{"name":"Marta Oliveira","email":"marta.oliveira@freshroute.io","title":"Head of Operations","company":"FreshRoute","website":"freshroute.io","source":"pricing page demo request","message":"We run 40 delivery vans and plan routes in spreadsheets. Looking at options."}""")
        ],
        DailyTokenLimit = 1_500_000,
        DailyCostLimitUsd = 6m
    };

    /// <summary>Content: research, outline and draft, then an editor and an SEO review in parallel before the final version.</summary>
    public static readonly WorkspaceTemplate ContentProduction = new()
    {
        Id = "content-production",
        Name = "Content production",
        Category = "Marketing",
        Description = "Give it a topic. It researches, outlines and drafts an article, has it edited and SEO-reviewed in parallel, " +
                      "and delivers a publish-ready final version.",
        Goal = "Produce well-researched, publish-ready articles in our voice.",
        SampleInput = "A practical guide for small online shops: how to cut cart abandonment in 2026.",
        Pipeline = new PipelineDefinition
        {
            MaxRunMinutes = 45,
            Stages =
            [
                Stage("research", "Research", "Content researcher",
                    "Research the topic: what readers need to know, current facts and statistics with sources, and what top-ranking " +
                    "articles cover and miss."),
                Stage("outline", "Outline", "Content strategist",
                    "Write the outline: working title, angle, audience, sections with key points, and where each source is used.",
                    ["research"]),
                Stage("draft", "Draft", "Writer",
                    "Write the article from the outline (1,200–1,800 words), in the voice described in the workspace's knowledge or a " +
                    "clear, friendly expert voice. Save it as draft.md.",
                    ["outline"], ["research", "filesystem"]),
                Stage("edit", "Edit", "Editor",
                    "Edit draft.md for clarity, structure, accuracy and voice. List the changes that matter.",
                    ["draft"], ["research", "filesystem"], onFailure: StageFailurePolicy.Continue),
                Stage("seo", "SEO review", "SEO specialist",
                    "Recommend a title, meta description, target keywords, headings and internal-link ideas, without hurting readability.",
                    ["draft"], ["research", "filesystem"], onFailure: StageFailurePolicy.Continue),
                Stage("final", "Final version", "Managing editor",
                    "Apply the edits and the SEO recommendations that help readers, and save article.md with its title and meta description.",
                    ["edit", "seo"], ["research", "filesystem"], retries: 0)
            ]
        },
        Safety = ResearchSafety,
        DailyTokenLimit = 2_000_000,
        DailyCostLimitUsd = 8m
    };

    /// <summary>Competitive intelligence: every Monday, competitors are scanned and the changes that matter are digested.</summary>
    public static readonly WorkspaceTemplate CompetitiveIntelligence = new()
    {
        Id = "competitive-intelligence",
        Name = "Weekly competitive intelligence",
        Category = "Research",
        Description = "Every Monday morning it scans your competitors (launches, pricing, hiring, funding, reviews), keeps what " +
                      "matters, and sends a short digest with what to do about it.",
        Goal = "Keep the team informed every week about what competitors did and what it means for us.",
        SampleInput = "Our competitors: Notion, Coda and Slite. We sell a team wiki for engineering teams.",
        Pipeline = new PipelineDefinition
        {
            MaxRunMinutes = 45,
            Stages =
            [
                Stage("scan", "Scan competitors", "Market scout",
                    "For each competitor named in the run's input or the workspace's knowledge, find what happened in the last 7 days: " +
                    "launches, pricing changes, hiring, funding, notable reviews and press. Link each item.",
                    helpers: 3),
                Stage("analyse", "What it means", "Strategy analyst",
                    "Keep only what matters to us; for each item, say why it matters and what we might do (respond, watch, ignore). " +
                    "Compare with last week's notes in memory, and save this week's key facts with write_memory.",
                    ["scan"]),
                Stage("digest", "Digest", "Digest writer",
                    "Write the weekly digest: three headlines, then a short section per competitor, then recommended actions. " +
                    "Under 400 words. Save it as digest.md.",
                    ["analyse"], ["research", "filesystem"], retries: 0)
            ]
        },
        Safety = ResearchSafety,
        Schedules = [new TemplateSchedule("Every Monday 08:00 UTC", "Write this week's competitive intelligence digest.", "0 8 * * 1")],
        DailyTokenLimit = 2_000_000,
        DailyCostLimitUsd = 8m
    };

    /// <summary>Legal operations: a contract is summarised, risk-reviewed and checked for obligations in parallel, then turned into a memo.</summary>
    public static readonly WorkspaceTemplate ContractReview = new()
    {
        Id = "contract-review",
        Name = "Contract review",
        Category = "Legal & finance",
        Description = "Paste or attach a contract. It summarises the terms, reviews risks against your playbook and lists " +
                      "obligations and dates in parallel, then writes a negotiation memo. Not legal advice: a lawyer decides.",
        Goal = "Review commercial contracts against our playbook and prepare negotiation notes for a lawyer.",
        SampleInput = "Review this SaaS order form: 3-year term, auto-renews for 3 more unless cancelled 180 days before; price " +
                      "increases up to 12% a year at the vendor's discretion; liability capped at fees paid in the prior 1 month; vendor may " +
                      "use customer data to improve its services; governed by Delaware law.",
        Pipeline = new PipelineDefinition
        {
            MaxRunMinutes = 30,
            Stages =
            [
                Stage("summary", "Summarise", "Contract analyst",
                    "Summarise the contract in the run's input (or the files it names): parties, term, renewal, pricing, payment, " +
                    "liability, IP, data, termination and governing law, quoting the clauses.",
                    capabilities: ["research", "filesystem"]),
                Stage("risks", "Risk review", "Risk reviewer",
                    "Compare the terms with the playbook in the workspace's knowledge (or common market positions): rate each deviation " +
                    "high, medium or low, and propose fallback wording.",
                    ["summary"], onFailure: StageFailurePolicy.Continue),
                Stage("obligations", "Obligations & dates", "Obligations tracker",
                    "List every obligation, deadline and notice period for both sides (e.g. the cancellation window), with dates where " +
                    "they can be worked out.",
                    ["summary"], onFailure: StageFailurePolicy.Continue),
                Stage("memo", "Negotiation memo", "Memo writer",
                    "Write memo.md for the lawyer: the five issues that matter most, the asks and fallbacks for each, the key dates, and " +
                    "open questions. State plainly that it is not legal advice.",
                    ["risks", "obligations"], ["research", "filesystem"], retries: 0)
            ]
        },
        Safety = ResearchSafety,
        DailyTokenLimit = 1_500_000,
        DailyCostLimitUsd = 6m
    };

    /// <summary>Recruiting: each application is assessed against the role's requirements only, and turned into an interview brief.</summary>
    public static readonly WorkspaceTemplate CandidateScreening = new()
    {
        Id = "candidate-screening",
        Name = "Candidate screening",
        Category = "People",
        Description = "Each application from your applicant tracking system is assessed against the role's requirements, with " +
                      "strengths and gaps found in parallel, and turned into an interview brief. A recruiter decides.",
        Goal = "Assess applications fairly against each role's requirements and prepare interviewers.",
        Pipeline = new PipelineDefinition
        {
            MaxRunMinutes = 20,
            MaxConcurrentRuns = 4,
            Stages =
            [
                Stage("profile", "Profile", "Recruiting coordinator",
                    "Extract the candidate's experience, skills and achievements, and the role's requirements (from the input or the " +
                    "workspace's knowledge). Leave out personal characteristics unrelated to the job (age, gender, ethnicity, religion, " +
                    "marital status, photos) and never use them."),
                Stage("strengths", "Strengths", "Skills assessor",
                    "Match the candidate's evidence to each requirement: met, partly met or not shown, quoting the evidence.",
                    ["profile"], onFailure: StageFailurePolicy.Continue),
                Stage("gaps", "Gaps & questions", "Gap assessor",
                    "Identify gaps or unclear points relevant to the job, and the interview questions that would resolve each one.",
                    ["profile"], onFailure: StageFailurePolicy.Continue),
                Stage("brief", "Interview brief", "Interview planner",
                    "Write the interview brief: a summary, a requirement-by-requirement assessment, the questions to ask, and a suggested " +
                    "next step (interview, hold or decline) with job-related reasons only. A recruiter makes the decision.",
                    ["strengths", "gaps"], retries: 0)
            ]
        },
        Safety = ReviewedWrites,
        Webhooks =
        [
            new TemplateWebhook("New applications", "Screen this application.",
                """{"role":"Senior Backend Engineer","requirements":["5+ years backend development","Go or Rust in production","distributed systems","on-call experience"],"candidate":{"name":"Jonas Weber","cv":"7 years backend at two fintechs; Go services handling 20k rps; led migration to Kafka; on-call lead for payments; some Rust side projects; BSc Computer Science."}}""")
        ],
        DailyTokenLimit = 1_500_000,
        DailyCostLimitUsd = 6m
    };
}
