using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentRuntime.Workspaces;

/// <summary>A form trigger's form: the fields a person fills in to start a run.</summary>
[GenerateSerializer]
public sealed record FormDefinition
{
    [Id(0)] public string Title { get; init; } = string.Empty;
    [Id(1)] public string Description { get; init; } = string.Empty;
    [Id(2)] public List<FormField> Fields { get; init; } = [];
    [Id(3)] public string SubmitLabel { get; init; } = "Submit";
    /// <summary>Shown after a submission.</summary>
    [Id(4)] public string SuccessMessage { get; init; } = "Thanks, your submission was received.";
}

/// <summary>One field of a form. Its answer reaches the run as <c>input.&lt;key&gt;</c>.</summary>
[GenerateSerializer]
public sealed record FormField
{
    /// <summary>Lowercase letters, digits and underscores, e.g. "company_name".</summary>
    [Id(0)] public required string Key { get; init; }
    [Id(1)] public required string Label { get; init; }
    /// <summary>One of <see cref="FormFieldTypes.All"/>.</summary>
    [Id(2)] public string Type { get; init; } = FormFieldTypes.Text;
    [Id(3)] public bool Required { get; init; }
    /// <summary>Choices for select and multiselect fields.</summary>
    [Id(4)] public List<string> Options { get; init; } = [];
    [Id(5)] public string? Placeholder { get; init; }
    [Id(6)] public string? Help { get; init; }
}

public static class FormFieldTypes
{
    public const string Text = "text";
    public const string Textarea = "textarea";
    public const string Number = "number";
    public const string Email = "email";
    public const string Url = "url";
    public const string Date = "date";
    public const string Select = "select";
    public const string MultiSelect = "multiselect";
    public const string Checkbox = "checkbox";

    public static readonly string[] All = [Text, Textarea, Number, Email, Url, Date, Select, MultiSelect, Checkbox];
}

/// <summary>A person's answers to a form, as the API received them.</summary>
[GenerateSerializer]
public sealed record FormSubmission
{
    [Id(0)] public required string TriggerId { get; init; }
    /// <summary>Answers by field key, as JSON (strings, numbers, booleans, lists of strings).</summary>
    [Id(1)] public required string AnswersJson { get; init; }
    /// <summary>The signed-in person (user id), recorded on the run.</summary>
    [Id(2)] public required string SubmittedBy { get; init; }
    [Id(3)] public string? SubmittedByName { get; init; }
    /// <summary>Chosen by the form page once per submission, so a double click or a retried
    /// request starts one run.</summary>
    [Id(4)] public string? SubmissionId { get; init; }
}

/// <summary>A form as the page that shows it needs it.</summary>
[GenerateSerializer]
public sealed record FormView
{
    [Id(0)] public required string WorkspaceId { get; init; }
    [Id(1)] public required string WorkspaceName { get; init; }
    [Id(2)] public required string TriggerId { get; init; }
    [Id(3)] public required string Name { get; init; }
    [Id(4)] public required FormDefinition Form { get; init; }
    /// <summary>False while the workspace is paused or archived, or the trigger switched off.</summary>
    [Id(5)] public bool Accepting { get; init; }
    [Id(6)] public long Submissions { get; init; }
}

[GenerateSerializer]
public sealed record FormSubmitResult
{
    [Id(0)] public bool Success { get; init; }
    [Id(1)] public string Message { get; init; } = string.Empty;
    /// <summary>Problems with particular fields, by key.</summary>
    [Id(2)] public Dictionary<string, string> FieldErrors { get; init; } = [];
    [Id(3)] public string? RunId { get; init; }
    [Id(4)] public int Number { get; init; }
    [Id(5)] public bool NotFound { get; init; }
}

/// <summary>Checks forms when they're made and answers when they're submitted. Answers are checked
/// here, on the server, whatever the page did: they're untrusted input to the run.</summary>
public static partial class FormValidator
{
    public const int MaxFields = 30;
    public const int MaxOptions = 50;
    public const int MaxTextLength = 500;
    public const int MaxTextareaLength = 10_000;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    /// <summary>Why the form can't be used; null when it can.</summary>
    public static string? Validate(FormDefinition? form)
    {
        if (form is null) return "A form trigger needs a form.";
        if (form.Fields.Count == 0) return "A form needs at least one field.";
        if (form.Fields.Count > MaxFields) return $"A form can have at most {MaxFields} fields.";
        if (form.Title.Length > 120 || form.Description.Length > 2000) return "The form's title or description is too long.";

        var keys = new HashSet<string>();
        foreach (var f in form.Fields)
        {
            if (!KeyPattern().IsMatch(f.Key)) return $"Field key '{f.Key}' must be lowercase letters, digits and underscores, starting with a letter.";
            if (!keys.Add(f.Key)) return $"Two fields have the key '{f.Key}'.";
            if (string.IsNullOrWhiteSpace(f.Label) || f.Label.Length > 200) return $"Field '{f.Key}' needs a label (200 characters at most).";
            if (!FormFieldTypes.All.Contains(f.Type)) return $"Field '{f.Key}' has an unknown type '{f.Type}'. Use one of: {string.Join(", ", FormFieldTypes.All)}.";
            if (f.Type is FormFieldTypes.Select or FormFieldTypes.MultiSelect)
            {
                if (f.Options.Count == 0) return $"Field '{f.Label}' needs options to choose from.";
                if (f.Options.Count > MaxOptions) return $"Field '{f.Label}' can have at most {MaxOptions} options.";
                if (f.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 200)) return $"Field '{f.Label}' has an empty or too long option.";
            }
        }

        return null;
    }

    /// <summary>A form with its text trimmed and option lists de-duplicated.</summary>
    public static FormDefinition Normalize(FormDefinition form) => form with
    {
        Title = form.Title.Trim(),
        Description = form.Description.Trim(),
        SubmitLabel = string.IsNullOrWhiteSpace(form.SubmitLabel) ? "Submit" : form.SubmitLabel.Trim()[..Math.Min(40, form.SubmitLabel.Trim().Length)],
        SuccessMessage = string.IsNullOrWhiteSpace(form.SuccessMessage) ? "Thanks, your submission was received." : form.SuccessMessage.Trim(),
        Fields = form.Fields.Select(f => f with
        {
            Label = f.Label.Trim(),
            Type = f.Type.Trim().ToLowerInvariant(),
            Options = f.Options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList()
        }).ToList()
    };

    /// <summary>The answers as data, one typed value per field; or what's wrong, by field key.</summary>
    public static (JsonObject? Answers, Dictionary<string, string> Errors) Check(FormDefinition form, JsonObject? submitted)
    {
        var answers = new JsonObject();
        var errors = new Dictionary<string, string>();
        submitted ??= [];
        foreach (var f in form.Fields)
        {
            var raw = submitted[f.Key];
            var (value, error) = CheckField(f, raw);
            if (error is not null) errors[f.Key] = error;
            else answers[f.Key] = value;
        }

        return (errors.Count == 0 ? answers : null, errors);
    }

    private static (JsonNode? Value, string? Error) CheckField(FormField f, JsonNode? raw)
    {
        var text = raw is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : raw is JsonValue other ? other.ToJsonString() : null;
        var empty = raw is null || (text is not null && text.Length == 0) || (raw is JsonArray a && a.Count == 0);

        if (f.Type == FormFieldTypes.Checkbox)
        {
            var on = raw is JsonValue b && (b.TryGetValue<bool>(out var flag) ? flag : text is "true" or "on" or "yes");
            return f.Required && !on ? (null, "Please tick this box.") : (JsonValue.Create(on), null);
        }

        if (empty) return f.Required ? (null, "This field is required.") : (null, null);

        switch (f.Type)
        {
            case FormFieldTypes.Number:
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? (JsonValue.Create(number), null)
                    : (null, "Enter a number.");
            case FormFieldTypes.Email:
                return text!.Length <= 254 && EmailPattern().IsMatch(text) ? (JsonValue.Create(text), null) : (null, "Enter an email address.");
            case FormFieldTypes.Url:
                return text!.Length <= 2000 && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    ? (JsonValue.Create(text), null)
                    : (null, "Enter a web address starting with http:// or https://.");
            case FormFieldTypes.Date:
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null)
                    : (null, "Enter a date (YYYY-MM-DD).");
            case FormFieldTypes.Select:
                return f.Options.Contains(text!) ? (JsonValue.Create(text), null) : (null, "Choose one of the options.");
            case FormFieldTypes.MultiSelect:
            {
                var chosen = raw is JsonArray list
                    ? list.Select(n => n is JsonValue x && x.TryGetValue<string>(out var c) ? c.Trim() : null).ToList()
                    : [text];
                if (chosen.Any(c => c is null || !f.Options.Contains(c))) return (null, "Choose from the options.");
                return (new JsonArray([.. chosen.Distinct().Select(c => (JsonNode?)JsonValue.Create(c))]), null);
            }
            case FormFieldTypes.Textarea:
                return text!.Length <= MaxTextareaLength ? (JsonValue.Create(text), null) : (null, $"At most {MaxTextareaLength:N0} characters.");
            default:
                return text!.Length <= MaxTextLength ? (JsonValue.Create(text), null) : (null, $"At most {MaxTextLength} characters.");
        }
    }

    /// <summary>The answers as text, for agent stages (which read the run's input as text).</summary>
    public static string Describe(FormDefinition form, JsonObject answers) =>
        string.Join("\n", form.Fields.Where(f => answers.ContainsKey(f.Key)).Select(f =>
        {
            var value = answers[f.Key] switch
            {
                JsonArray a => string.Join(", ", a.Select(x => x?.GetValue<string>())),
                JsonValue x when x.TryGetValue<bool>(out var b) => b ? "yes" : "no",
                JsonValue x when x.TryGetValue<string>(out var s) => s,
                var other => other?.ToJsonString() ?? ""
            };
            return $"- {f.Label}: {value}";
        }));
}
