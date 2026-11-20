using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// A review page per work slice, a milestone or a task (#106). A person found real visual bugs that
/// agents' reviews had passed: a lighting bug lived for five milestones because the screenshots were
/// taken at settings that draw no shadows, and reviews ended in long "decisions I made" and "not
/// checked" lists spread across replies and files. So an agent puts what the person should look at
/// on one page with write_review: what the slice did, its pictures, short clips and compare renders,
/// each with the render settings it was taken at, the decisions it made, and what it didn't check.
/// The person opens it from the slice on the Tasks page, accepts it or asks for changes, and
/// comments; read_review and the next briefing give the agent what they said. Kept as a record
/// beside the milestone or task, named as it is, and synced as they are.
/// </summary>
public partial class MainWindow
{
    private const string ReviewPage = "Review";
    private const double ReviewWidth = 1040;
    private const int MostShots = 24;
    private const int MostReviewLines = 40;
    private const int LongestReviewLine = 600;
    private const long LongestClip = 200L * 1024 * 1024;
    private const string ByThePerson = "person";

    private static readonly string[] ClipEndings = [".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v", ".wmv"];
    private static readonly string[] ShotFields = ["path", "caption", "settings", "kind", "before"];
    private static readonly string[] ShotKinds = [ReviewShot.PictureKind, ReviewShot.ClipKind, ReviewShot.CompareKind];

    private (string ProjectId, string SliceId)? _openReview;

    // What the person has typed in a page's comment box, kept while the page is drawn again, as it is on each sync.
    private readonly Dictionary<string, string> _reviewDrafts = [];

    // Pictures as drawn, by their id, as a page of them is drawn again on each sync.
    private readonly Dictionary<string, ImageSource?> _reviewPictures = [];

    private List<McpTool> ReviewTools() =>
    [
        new("write_review", "Fills the review page of a work slice, a milestone or a task, for the person to judge: what it did, the pictures, short clips and compare renders to look at, the decisions you made and what you didn't check, all on one page they open from the slice on the Tasks page, where they accept it or ask for changes. "
            + "Each picture says the render settings it was taken at, as one taken at settings that draw no shadows can't show a lighting bug. It replaces what's on the page, and keeps the person's comments; mode append adds to it. Either way it waits for the person again. read_review gives what they said.",
            new JsonObject
            {
                ["milestone"] = Text("The milestone it's the review of, by its name or the start of it."),
                ["task"] = Text("Or the task it's the review of, by its id from list_tasks."),
                ["summary"] = Text("What the slice did and how it was checked, in a few lines, for the person reading the page."),
                ["shots"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = $"What to look at, up to {MostShots}, each an object: path (a picture or a short clip, by full path or a path in the project's folder), caption (what it shows and what to look for), "
                        + "settings (the render settings it was taken at, such as quality, shadows and lighting, resolution and camera), kind (picture, clip or compare, from the file when not given) and, for a compare, before (the picture to set it beside, such as the reference or the last render). "
                        + "Pictures are kept in Agent Orange, so every PC sees them; a clip is kept by its path, so put it in the project's folder to have it travel with the repo.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = Text("The picture or clip."),
                            ["caption"] = Text("What it shows, and what to look for."),
                            ["settings"] = Text("The render settings it was taken at."),
                            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. ShotKinds.Select(kind => (JsonNode)kind)]) },
                            ["before"] = Text("For a compare: the picture to set it beside."),
                        },
                        ["required"] = new JsonArray("path", "caption", "settings"),
                    },
                },
                ["decisions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "The decisions you made that the person may want to change, a line each." },
                ["not_checked"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "What you didn't check, or couldn't, a line each, so the person knows where to look themselves." },
                ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("replace", "append"), ["description"] = "replace or append: the page is these, or these are added to what's there. replace when not given." },
            },
            [], (project, given) => WriteReviewFor(project, given)),
        new("read_review", "A work slice's review page: what's on it, and the person's answer, accepted or changes asked, with their comments. Read it before going on with a slice they've looked at.",
            new JsonObject { ["milestone"] = Text("The milestone, by its name or the start of it."), ["task"] = Text("Or the task, by its id from list_tasks.") },
            [], (project, given) => ReadReviewFor(project, given)),
    ];

    // The slice a review page is for: its id, what it's called, and whether it's a task.
    private (string Id, string Name, bool IsTask) ReviewSlice(Project project, JsonObject given, string tool)
    {
        var stage = _database.ProjectStage(project.Id);
        var taskAsked = Given(given, "task") is { Length: > 0 } task ? task : null;
        var milestoneAsked = Given(given, "milestone") is { Length: > 0 } milestone ? milestone : null;
        if (taskAsked is not null && milestoneAsked is not null) throw new McpException(-32602, $"{tool} is about one work slice: give milestone or task, not both.");
        if (taskAsked is not null)
        {
            var found = _database.ListTasks(project.Id, stage).FirstOrDefault(each => SameId(each.Id, taskAsked));
            if (found.Task is null) throw new McpException(-32602, $"task: there's no task {taskAsked}. list_tasks gives each task's id, after the word task, such as (task 3f2a9c). Or give milestone instead.");
            return (found.Id, found.Task.Title, true);
        }
        var milestones = _database.ListMilestones(project.Id, stage);
        if (milestoneAsked is null)
        {
            throw new McpException(-32602, milestones.Count == 0
                ? $"{tool} needs milestone or task: the work slice the page is for. The stage has no milestones, so give the task, by its id from list_tasks."
                : $"{tool} needs milestone or task: the work slice the page is for. The milestones: {string.Join("; ", milestones.Take(ListedNames).Select(each => each.Milestone.Name))}. Or give the task, by its id from list_tasks.");
        }
        var id = MilestoneId(project, milestoneAsked)!;
        return (id, milestones.First(each => each.Id == id).Milestone.Name, false);
    }

    private static string Nth(int number) => number switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{number}th",
    };

    private sealed record ShotAsked(string Kind, string Caption, string Settings, string File, string? Before);

    // A file a review shows, found by its full path or one in the project's folder.
    private string ReviewFile(Project project, string path, string argument)
    {
        var folder = ProjectFolder(project.Id);
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) || folder is null ? path : System.IO.Path.Combine(folder, path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpException(-32602, $"{argument}: {path} isn't a path to a file. Give its full path, or one inside the project's folder.");
        }
        return File.Exists(full)
            ? full
            : throw new McpException(-32602, $"{argument}: there's no file at {path}{(System.IO.Path.IsPathRooted(path) || folder is null ? "" : $" in the project's folder, {folder}")}. Give its full path, or one inside the project's folder.");
    }

    private static bool IsClip(string file) => ClipEndings.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant());

    // A list given as one, as JSON written out as text, or as one object or line.
    private static JsonArray? ListGiven(JsonNode? given, string argument, string what)
    {
        if (given is null) return null;
        if (given is JsonArray list) return list;
        if (given is JsonValue value && value.TryGetValue<string>(out var text))
        {
            text = text.Trim();
            if (text.StartsWith('['))
            {
                try
                {
                    if (JsonNode.Parse(text) is JsonArray parsed) return parsed;
                }
                catch (JsonException)
                {
                }
                throw new McpException(-32602, $"{argument} is a list of {what}; it was text that starts like a list but isn't one.");
            }
            return text.Length == 0 ? [] : [JsonValue.Create(text)];
        }
        if (given is JsonObject one) return [one.DeepClone()];
        throw new McpException(-32602, $"{argument} is a list of {what}; it was {given.ToJsonString()}.");
    }

    // What to look at, each checked before anything is kept, so a mistake in one keeps none.
    private List<ShotAsked> ShotsAsked(Project project, JsonNode? given)
    {
        var list = ListGiven(given, "shots", "objects, each with path, caption and settings") ?? [];
        if (list.Count > MostShots) throw new McpException(-32602, $"shots: a review page shows up to {MostShots}, and {list.Count} were given. Give the ones that show the slice best.");
        var shots = new List<ShotAsked>();
        for (var at = 0; at < list.Count; at++)
        {
            var which = $"shots: the {Nth(at + 1)}";
            if (list[at] is not JsonObject shot) throw new McpException(-32602, $"{which} isn't an object. Each is an object with path, caption and settings, such as {{\"path\": \"shots/cave.png\", \"caption\": \"The cave at dusk\", \"settings\": \"High quality, shadows on, 1920x1080\"}}.");
            foreach (var (field, _) in shot.Where(each => !ShotFields.Contains(each.Key)))
            {
                throw new McpException(-32602, $"{which} has no field called {field}{(Closest(field, ShotFields) is { } close ? $" (did you mean {close}?)" : "")}. A shot's fields: path (needed), caption (needed), settings (needed), kind, before.");
            }
            string? Field(string name) => shot[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text.Trim() : null;
            var path = Field("path") ?? throw new McpException(-32602, $"{which} needs path: the picture or clip to look at.");
            which = $"{which} ({path})";
            var file = ReviewFile(project, path, "shots");
            var before = Field("before") is { } beforePath ? ReviewFile(project, beforePath, "shots") : null;
            var kind = Field("kind")?.ToLowerInvariant() ?? (before is not null ? ReviewShot.CompareKind : IsPicture(file) ? ReviewShot.PictureKind : IsClip(file) ? ReviewShot.ClipKind : null)
                ?? throw new McpException(-32602, $"{which} is neither a picture nor a clip. Pictures: {string.Join(", ", DocumentPage.PictureEndings)}; clips: {string.Join(", ", ClipEndings)}. Put other files in the summary, or as a task's evidence.");
            if (!ShotKinds.Contains(kind)) throw new McpException(-32602, $"{which}: kind is one of {string.Join(", ", ShotKinds)}; it was {kind}.");
            if (kind != ReviewShot.ClipKind && !IsPicture(file)) throw new McpException(-32602, $"{which} is a {kind}, so its path is a picture: {string.Join(", ", DocumentPage.PictureEndings)}.");
            if (kind == ReviewShot.ClipKind && !IsClip(file)) throw new McpException(-32602, $"{which} is a clip, so its path is a video: {string.Join(", ", ClipEndings)}.");
            if (kind == ReviewShot.ClipKind && new FileInfo(file).Length > LongestClip) throw new McpException(-32602, $"{which} is {new FileInfo(file).Length / (1024 * 1024)} MB, which isn't a short clip. Keep clips to a few seconds each, under {LongestClip / (1024 * 1024)} MB.");
            if (kind == ReviewShot.CompareKind && before is null) throw new McpException(-32602, $"{which} is a compare, so it needs before: the picture to set it beside, such as the reference or the last render.");
            if (kind != ReviewShot.CompareKind && before is not null) throw new McpException(-32602, $"{which}: before is for a compare, and its kind is {kind}. Leave before out, or make its kind compare.");
            if (before is not null && !IsPicture(before)) throw new McpException(-32602, $"{which}: before is a picture to set it beside: {string.Join(", ", DocumentPage.PictureEndings)}.");
            var caption = Field("caption") ?? throw new McpException(-32602, $"{which} needs caption: what it shows, and what to look for.");
            var settings = Field("settings") ?? throw new McpException(-32602, $"{which} needs settings: the render settings it was taken at, such as quality, shadows and lighting, resolution and camera. A picture taken at settings that draw no shadows can't show a lighting bug, so the person needs to know.");
            shots.Add(new ShotAsked(kind, caption, settings, file, before));
        }
        return shots;
    }

    // A list of lines, such as the decisions made, or null when not given.
    private static List<string>? ReviewLines(JsonNode? given, string argument)
    {
        if (ListGiven(given, argument, "lines, each as text") is not { } list) return null;
        var lines = new List<string>();
        foreach (var each in list)
        {
            if (each is not JsonValue value || !value.TryGetValue<string>(out var text)) throw new McpException(-32602, $"{argument} is a list of lines, each as text; one was {each?.ToJsonString() ?? "null"}.");
            lines.AddRange(text.Split('\n').Select(line => line.Trim().TrimStart('-', '*', '•').Trim()).Where(line => line.Length > 0));
        }
        if (lines.Count > MostReviewLines) throw new McpException(-32602, $"{argument}: a review page keeps up to {MostReviewLines}, and {lines.Count} were given. Give the ones that matter most.");
        if (lines.FirstOrDefault(line => line.Length > LongestReviewLine) is { } longLine) throw new McpException(-32602, $"{argument}: each is a line, up to {LongestReviewLine} letters; one was {longLine.Length}, starting \"{longLine[..60]}…\".");
        return lines;
    }

    private static string Counted(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    // Such as "2 pictures, 1 clip and 1 compare".
    private static string ShotCounts(IReadOnlyCollection<ReviewShot> shots)
    {
        var parts = new[] { (ReviewShot.PictureKind, "picture", "pictures"), (ReviewShot.ClipKind, "clip", "clips"), (ReviewShot.CompareKind, "compare", "compares") }
            .Select(each => (Count: shots.Count(shot => shot.Kind == each.Item1), each.Item2, each.Item3)).Where(each => each.Count > 0).Select(each => Counted(each.Count, each.Item2, each.Item3)).ToList();
        return parts.Count == 0 ? "nothing to look at" : parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";
    }

    private string WriteReviewFor(Project project, JsonObject given)
    {
        var (sliceId, name, isTask) = ReviewSlice(project, given, "write_review");
        var append = Given(given, "mode") is "append";
        var summary = Given(given, "summary") is { Length: > 0 } words ? words : null;
        var shots = ShotsAsked(project, given["shots"]);
        var decisions = ReviewLines(given["decisions"], "decisions");
        var notChecked = ReviewLines(given["not_checked"], "not_checked");
        var old = _database.GetReview(sliceId);
        append &= old is not null;
        if (!append && summary is null && shots.Count == 0)
            throw new McpException(-32602, "write_review needs summary or shots: what the slice did, and what to look at. With mode append, it adds to the page there.");
        if (append && (old!.Shots?.Length ?? 0) + shots.Count > MostShots)
            throw new McpException(-32602, $"shots: a review page shows up to {MostShots}, and it has {old.Shots?.Length ?? 0}. Write the page again without mode append, with the ones that matter.");
        foreach (var (lines, argument, had) in new[] { (decisions, "decisions", old?.Decisions), (notChecked, "not_checked", old?.NotChecked) })
        {
            if (append && lines is not null && (had?.Length ?? 0) + lines.Count > MostReviewLines)
                throw new McpException(-32602, $"{argument}: a review page keeps up to {MostReviewLines}, and it has {had?.Length ?? 0}. Write the page again without mode append, with the ones that matter.");
        }

        // Every picture is read before any is kept, so a bad one keeps nothing.
        byte[] Prepared(string file) => ProjectImages.PrepareForBoard(file) ?? throw new McpException(-32602, $"shots: {file} couldn't be read as a picture. Give a PNG or JPEG.");
        var prepared = shots.Select(shot => (Shot: shot, Picture: shot.Kind == ReviewShot.ClipKind ? null : Prepared(shot.File), Before: shot.Before is { } before ? Prepared(before) : null)).ToList();
        var folder = ProjectFolder(project.Id);
        var kept = prepared.Select(each => new ReviewShot(each.Shot.Kind, each.Shot.Caption, each.Shot.Settings,
            each.Picture is { } picture ? _database.AddDocumentPicture(project.Id, picture) : null, InFolderOrWhole(folder, each.Shot.File),
            each.Before is { } beforePicture ? _database.AddDocumentPicture(project.Id, beforePicture) : null, each.Shot.Before is { } beforeFile ? InFolderOrWhole(folder, beforeFile) : null)).ToArray();

        var working = _conversations?.Where(each => each.ProjectId == project.Id && each.IsWorking).ToList() ?? [];
        var from = working.Count == 1 && working[0].Title.Length > 0 ? working[0].Title : null;
        var at = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        var review = append
            ? old! with
            {
                Summary = summary ?? old.Summary,
                Shots = [.. old.Shots ?? [], .. kept],
                Decisions = [.. old.Decisions ?? [], .. decisions ?? []],
                NotChecked = [.. old.NotChecked ?? [], .. notChecked ?? []],
                At = at,
                From = from ?? old.From,
                Verdict = null,
                VerdictAt = null,
            }
            : new SliceReview(_database.ProjectStage(project.Id), summary, kept, [.. decisions ?? []], [.. notChecked ?? []], at, from, Comments: old?.Comments);
        if (!append && old is not null)
        {
            foreach (var picture in LocalDatabase.ReviewPictures(old)) _database.DeleteDocumentPicture(picture);
        }
        _database.SaveReview(sliceId, review);
        McpChanged(project);

        var said = new StringBuilder($"{(append ? "Added to" : "Wrote")} the review page of the {(isTask ? "task" : "milestone")} {name}. It has {ShotCounts(review.Shots ?? [])}, "
            + $"{Counted(review.Decisions?.Length ?? 0, "decision", "decisions")} and {Counted(review.NotChecked?.Length ?? 0, "thing", "things")} not checked. ");
        said.Append(old?.Verdict is { } before ? $"The person had {VerdictWords(before)} it; it waits for them again, with their comments kept. " : "It waits for the person, ");
        said.Append("who opens it from the slice on the Tasks page and accepts it or asks for changes there. read_review gives what they said.");
        if ((review.NotChecked?.Length ?? 0) == 0) said.Append(" It says nothing about what wasn't checked: if anything wasn't, add it with not_checked and mode append, so the person knows where to look.");
        return said.ToString();
    }

    private static string VerdictWords(string verdict) => verdict == SliceReview.Accepted ? "accepted" : "asked for changes on";

    private static string When(string at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : at;

    private static DateTimeOffset Moment(string at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : DateTimeOffset.MinValue;

    private static string WhoSaid(ReviewComment comment, bool forAgent) => comment.Who == ByThePerson ? forAgent ? "The person" : "You" : comment.Who;

    private string ReadReviewFor(Project project, JsonObject given)
    {
        var (sliceId, name, isTask) = ReviewSlice(project, given, "read_review");
        var slice = $"the {(isTask ? "task" : "milestone")} {name}";
        if (_database.GetReview(sliceId) is not { } review) return $"{slice[..1].ToUpperInvariant()}{slice[1..]} has no review page yet. write_review fills it.";
        var said = new StringBuilder();
        said.AppendLine($"# Review of {slice}");
        said.AppendLine($"Written {When(review.At)}{(review.From is { Length: > 0 } from ? $", from {from}" : "")}.");
        said.AppendLine(review.Verdict switch
        {
            SliceReview.Accepted => $"The person accepted it, {When(review.VerdictAt ?? review.At)}.",
            SliceReview.ChangesAsked => $"The person asked for changes, {When(review.VerdictAt ?? review.At)}: their comments say what.",
            _ => "It waits for the person: they haven't accepted it or asked for changes yet.",
        });
        if (review.Summary is { Length: > 0 } summary) said.AppendLine().AppendLine("## What it did").AppendLine(summary.Trim());
        if (review.Shots is { Length: > 0 } shots)
        {
            said.AppendLine().AppendLine("## To look at");
            foreach (var (shot, at) in shots.Select((shot, at) => (shot, at + 1)))
            {
                var what = shot.Kind switch
                {
                    ReviewShot.ClipKind => $"the clip {shot.Path}",
                    ReviewShot.CompareKind => $"picture:{shot.Picture} beside picture:{shot.BeforePicture} (from {shot.Path}, beside {shot.BeforePath})",
                    _ => $"picture:{shot.Picture} (from {shot.Path})",
                };
                said.AppendLine($"{at}. {char.ToUpperInvariant(shot.Kind[0])}{shot.Kind[1..]}: {shot.Caption} Taken at: {shot.Settings}. {what}");
            }
            said.AppendLine("read_picture shows a picture.");
        }
        if (review.Decisions is { Length: > 0 } decisions) said.AppendLine().AppendLine("## Decisions made").AppendLine(string.Join("\n", decisions.Select(line => $"- {line}")));
        if (review.NotChecked is { Length: > 0 } notChecked) said.AppendLine().AppendLine("## Not checked").AppendLine(string.Join("\n", notChecked.Select(line => $"- {line}")));
        if (review.Comments is { Length: > 0 } comments)
        {
            said.AppendLine().AppendLine("## Comments");
            foreach (var comment in comments) said.AppendLine($"- {WhoSaid(comment, forAgent: true)}, {When(comment.At)}: {comment.Text.Replace("\n", " ")}");
        }
        return said.ToString();
    }

    /// <summary>
    /// The briefing's section on review pages the person answered since they were written: the
    /// newest where they asked for changes or commented, or null when there are none.
    /// </summary>
    private string? ReviewsForBriefing(string projectId)
    {
        var stage = _database.ProjectStage(projectId);
        var milestones = _database.ListMilestones(projectId, stage).ToDictionary(each => each.Id, each => each.Milestone.Name);
        var tasks = _database.ListTasks(projectId, stage).ToDictionary(each => each.Id, each => each.Task);
        var answered = new List<(string Where, SliceReview Review, List<ReviewComment> Said)>();
        foreach (var (sliceId, review) in _database.ListReviews(projectId, stage))
        {
            var where = milestones.TryGetValue(sliceId, out var milestone) ? $"the milestone {milestone}" : tasks.TryGetValue(sliceId, out var task) && !task.Done ? $"the task {task.Title} (task {Short(sliceId)})" : null;
            if (where is null) continue;
            var since = (review.Comments ?? []).Where(comment => comment.Who == ByThePerson && Moment(comment.At) > Moment(review.At)).ToList();
            if (review.Verdict == SliceReview.ChangesAsked || since.Count > 0) answered.Add((where, review, since));
        }
        if (answered.Count == 0) return null;
        var said = new StringBuilder();
        said.AppendLine("## What the person said on review pages");
        said.AppendLine();
        said.AppendLine("The person answered these review pages since they were written. Start from what they asked; read_review gives the whole page.");
        foreach (var (where, review, comments) in answered.OrderByDescending(each => Moment(each.Review.VerdictAt ?? each.Review.At)).Take(HandoffsInBriefing))
        {
            said.AppendLine();
            said.AppendLine($"### On {where}{(review.Verdict == SliceReview.ChangesAsked ? $", they asked for changes, {When(review.VerdictAt ?? review.At)}" : review.Verdict == SliceReview.Accepted ? ", they accepted it" : "")}");
            foreach (var comment in comments) said.AppendLine($"- {comment.Text.Replace("\n", " ").Trim()}");
        }
        return said.ToString().TrimEnd();
    }

    // The page

    /// <summary>Opens a slice's review page, from the Tasks page.</summary>
    private void OpenReview(Project project, string sliceId)
    {
        _openSection = (project.Id, ReviewPage);
        _openCard = null;
        _openReview = (project.Id, sliceId);
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
    }

    // The milestone or task a review page is for, by its id, in the stage the project is at.
    private (string Name, bool IsTask)? SliceNamed(Project project, string sliceId)
    {
        var stage = _database.ProjectStage(project.Id);
        if (_database.ListMilestones(project.Id, stage).FirstOrDefault(each => each.Id == sliceId) is { Milestone: not null } milestone) return (milestone.Milestone.Name, false);
        if (_database.ListTasks(project.Id, stage).FirstOrDefault(each => each.Id == sliceId) is { Task: not null } task) return (task.Task.Title, true);
        return null;
    }

    private (string SliceId, string Name, bool IsTask)? OpenReviewOf(Project project) =>
        _openReview is { } open && open.ProjectId == project.Id && SliceNamed(project, open.SliceId) is { } slice ? (open.SliceId, slice.Name, slice.IsTask) : null;

    /// <summary>How a review stands, in a word or two, as the Tasks page shows it beside its slice.</summary>
    private static string ReviewState(SliceReview review) => review.Verdict switch
    {
        SliceReview.Accepted => "accepted",
        SliceReview.ChangesAsked => "changes asked",
        _ => "to look at",
    };

    private Brush ReviewBrush(SliceReview review) => (Brush)FindResource(review.Verdict switch
    {
        SliceReview.Accepted => "Accent",
        SliceReview.ChangesAsked => "StatusInProgress",
        _ => "StatusNeedsReview",
    });

    /// <summary>The link to a slice's review page, for its milestone header or task line.</summary>
    private Button ReviewLink(Project project, string sliceId, string sliceName, SliceReview review)
    {
        var link = TextButton($"Review · {ReviewState(review)}", () => OpenReview(project, sliceId));
        link.Foreground = ReviewBrush(review);
        link.FontSize = 12.5;
        link.ToolTip = $"Open the review page of {sliceName}: its pictures, clips, decisions and what wasn't checked.";
        AutomationProperties.SetName(link, $"Open the review of {sliceName}");
        return link;
    }

    private void AddReviewPage(StackPanel body, Project project)
    {
        body.MaxWidth = ReviewWidth;
        if (OpenReviewOf(project) is not { } slice || _database.GetReview(slice.SliceId) is not { } review)
        {
            body.Children.Add(CenteredNote("This review page isn't here: its milestone or task may have been deleted, or be in another stage."));
            return;
        }
        var sliceId = slice.SliceId;

        // What it's the review of, how it stands, and accepting it, at the top.
        var state = new Border
        {
            BorderBrush = ReviewBrush(review),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = review.Verdict switch
                {
                    SliceReview.Accepted => $"Accepted {When(review.VerdictAt ?? review.At)}",
                    SliceReview.ChangesAsked => $"Changes asked {When(review.VerdictAt ?? review.At)}",
                    _ => "Waiting for you",
                },
                FontSize = 13,
                Foreground = ReviewBrush(review),
            },
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { state } };
        if (review.Verdict is null)
        {
            var accept = PrimaryButton("Accept", () => AnswerReview(project, sliceId, SliceReview.Accepted, Draft(sliceId)));
            accept.Margin = new Thickness(10, 0, 0, 0);
            accept.ToolTip = "Accept the slice as it is. Anything typed in the comment box below goes with it.";
            AutomationProperties.SetName(accept, "Accept the review");
            actions.Children.Add(accept);
        }
        else
        {
            var reopen = TextButton("Reopen", () => AnswerReview(project, sliceId, null, null, reopen: true));
            reopen.Margin = new Thickness(6, 0, 0, 0);
            reopen.ToolTip = "Take your answer back, so it waits for you again.";
            AutomationProperties.SetName(reopen, "Reopen the review");
            actions.Children.Add(reopen);
        }
        var heading = new DockPanel { Margin = new Thickness(0, 22, 0, 4) };
        DockPanel.SetDock(actions, Dock.Right);
        heading.Children.Add(actions);
        heading.Children.Add(new TextBlock { Text = $"Review of {slice.Name}", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0) });
        body.Children.Add(heading);
        var counts = $"{ShotCounts(review.Shots ?? [])} · {Counted(review.Decisions?.Length ?? 0, "decision", "decisions")} · {Counted(review.NotChecked?.Length ?? 0, "thing", "things")} not checked";
        body.Children.Add(Note($"The {(slice.IsTask ? "task's" : "milestone's")} review, written {When(review.At)}{(review.From is { Length: > 0 } from ? $" by {from}" : "")} · {counts}", 0));
        if (review.Summary is { Length: > 0 } summary) body.Children.Add(new TextBlock { Text = summary.Trim(), FontSize = 14.5, Foreground = (Brush)FindResource("TextNormal"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 22 });

        // What wasn't checked comes before the pictures, as that's where the person's own look counts most.
        if (review.NotChecked is { Length: > 0 } notChecked)
        {
            var lines = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            title.Children.Add(AgentIcons.Line("triangle-alert", 16, (Brush)FindResource("StatusInProgress"), 2));
            title.Children.Add(new TextBlock { Text = "Not checked: look at these yourself", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("StatusInProgress"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            lines.Children.Add(title);
            foreach (var line in notChecked) lines.Children.Add(Bullet(line, "TextNormal"));
            body.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("StatusInProgress"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 10),
                Margin = new Thickness(0, 18, 0, 0),
                Child = lines,
            });
        }

        if (review.Shots is { Length: > 0 } shots)
        {
            body.Children.Add(ReviewHeading("To look at"));
            foreach (var (shot, at) in shots.Select((shot, at) => (shot, at + 1))) body.Children.Add(ShotView(project, shot, at));
        }
        if (review.Decisions is { Length: > 0 } decisions)
        {
            body.Children.Add(ReviewHeading("Decisions made"));
            body.Children.Add(Note("Choices the agent made that you may want to change. Say which in a comment.", 0));
            foreach (var line in decisions) body.Children.Add(Bullet(line, "TextNormal"));
        }

        body.Children.Add(ReviewHeading("Comments"));
        foreach (var said in review.Comments ?? [])
        {
            body.Children.Add(new TextBlock { Text = $"{WhoSaid(said, forAgent: false)} · {When(said.At)}", FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 8, 0, 2) });
            body.Children.Add(new TextBlock { Text = said.Text.Trim(), FontSize = 14, Foreground = (Brush)FindResource("TextNormal"), TextWrapping = TextWrapping.Wrap });
        }
        if (review.Comments is not { Length: > 0 }) body.Children.Add(Note("No comments yet. The agent reads them with read_review, and its next conversation's briefing has them.", 0));

        // As the task form's notes are.
        var box = Field(_reviewDrafts.GetValueOrDefault(sliceId) ?? "");
        box.Width = double.NaN;
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.MinHeight = 60;
        box.MaxHeight = 200;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetName(box, "Comment on the review");
        box.TextChanged += (_, _) => _reviewDrafts[sliceId] = box.Text;
        body.Children.Add(new TextBlock { Text = "Your comment: what to change, or anything to say with Accept", FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 16, 0, 4) });
        body.Children.Add(box);
        var comment = ControlButton("Comment", () => { if (Draft(sliceId) is { } text) AnswerReview(project, sliceId, null, text); });
        comment.ToolTip = "Add what's in the box as a comment, and leave the review as it stands.";
        var changes = ControlButton("Ask for changes", () =>
        {
            if (Draft(sliceId) is { } text) AnswerReview(project, sliceId, SliceReview.ChangesAsked, text);
            else Dialogs.Notify("Say what to change in the comment box first, so the agent knows.");
        });
        changes.Margin = new Thickness(8, 0, 0, 0);
        changes.ToolTip = "Send it back to the agent, with what's in the box as what to change.";
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 40), Children = { comment, changes } });
    }

    private string? Draft(string sliceId) => _reviewDrafts.TryGetValue(sliceId, out var text) && text.Trim().Length > 0 ? text.Trim() : null;

    private TextBlock ReviewHeading(string text)
    {
        var heading = Section(text);
        heading.Margin = new Thickness(0, 30, 0, 10);
        return heading;
    }

    private TextBlock Bullet(string text, string brush) => new()
    {
        Text = $"•  {text}",
        FontSize = 14,
        Foreground = (Brush)FindResource(brush),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 4),
    };

    // The person's answer: accepted or changes asked, a comment, or taking an answer back.
    private void AnswerReview(Project project, string sliceId, string? verdict, string? comment, bool reopen = false)
    {
        if (_database.GetReview(sliceId) is not { } review) return; // The newest, as an agent may have written it again meanwhile.
        var now = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        var comments = comment is { Length: > 0 } ? [.. review.Comments ?? [], new ReviewComment(comment, now, ByThePerson)] : review.Comments;
        review = reopen ? review with { Verdict = null, VerdictAt = null } : review with { Verdict = verdict ?? review.Verdict, VerdictAt = verdict is null ? review.VerdictAt : now, Comments = comments };
        _database.SaveReview(sliceId, review);
        if (comment is not null) _reviewDrafts.Remove(sliceId);
        _ = _sync.SyncNowAsync();
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
    }

    private ImageSource? ReviewPicture(string? id)
    {
        if (id is null) return null;
        if (_reviewPictures.TryGetValue(id, out var shown)) return shown;
        var stored = _database.GetDocumentPicture(id);
        var picture = stored is null ? null : ProjectImages.ToImage(Convert.FromBase64String(stored));
        if (picture is not null) _reviewPictures[id] = picture; // Not one that hasn't arrived yet, which may come with the next sync.
        return picture;
    }

    // A picture as big as the page allows, opening larger on a click.
    private FrameworkElement PictureView(Project project, string? pictureId, string? path, double tallest, string named)
    {
        var picture = ReviewPicture(pictureId);
        FrameworkElement shown = picture is null
            ? new Border { Height = 160, Background = (Brush)FindResource("SidebarBackground"), CornerRadius = new CornerRadius(6), Child = new TextBlock { Text = "This picture hasn't reached this PC yet.", Foreground = (Brush)FindResource("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }
            : new Image { Source = picture, Stretch = Stretch.Uniform, MaxHeight = tallest, HorizontalAlignment = HorizontalAlignment.Left };
        // Its button's own padding is taken back, so the picture lines up with the words under it.
        var open = new Button { Style = (Style)FindResource("HeaderButton"), Content = shown, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-7, -4, -7, -4), ToolTip = "Open it larger" };
        AutomationProperties.SetName(open, $"Open {named} larger");
        open.Click += (_, _) => OpenEvidence(project, new TaskEvidence("", pictureId, path));
        return open;
    }

    private FrameworkElement ShotView(Project project, ReviewShot shot, int number)
    {
        var view = new StackPanel { Margin = new Thickness(0, 0, 0, 26) };
        switch (shot.Kind)
        {
            case ReviewShot.CompareKind:
                var sides = new Grid();
                sides.ColumnDefinitions.Add(new ColumnDefinition());
                sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                sides.ColumnDefinitions.Add(new ColumnDefinition());
                FrameworkElement Side(string label, string? picture, string? path, int column)
                {
                    var side = new StackPanel();
                    side.Children.Add(new TextBlock { Text = $"{label}{(path is null ? "" : $" · {System.IO.Path.GetFileName(path)}")}", FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 0, 0, 4) });
                    side.Children.Add(PictureView(project, picture, path, 420, $"{label.ToLowerInvariant()} picture {number}"));
                    Grid.SetColumn(side, column);
                    return side;
                }
                sides.Children.Add(Side("Before", shot.BeforePicture, shot.BeforePath, 0));
                sides.Children.Add(Side("Now", shot.Picture, shot.Path, 2));
                view.Children.Add(sides);
                break;
            case ReviewShot.ClipKind:
                var play = new Polygon { Points = new PointCollection { new Point(0, 0), new Point(22, 13), new Point(0, 26) }, Fill = (Brush)FindResource("TextStrong"), VerticalAlignment = VerticalAlignment.Center };
                var tile = new Border
                {
                    Width = 420,
                    Height = 236,
                    CornerRadius = new CornerRadius(6),
                    Background = (Brush)FindResource("SidebarBackground"),
                    BorderBrush = (Brush)FindResource("Line"),
                    BorderThickness = new Thickness(1),
                    Child = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = (Brush)FindResource("ControlBackground"), Padding = new Thickness(20, 15, 14, 15), Child = play },
                            new TextBlock { Text = "Play the clip", FontSize = 13, Foreground = (Brush)FindResource("TextNormal"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) },
                            new TextBlock { Text = System.IO.Path.GetFileName(shot.Path ?? ""), FontSize = 12, Foreground = (Brush)FindResource("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center },
                        },
                    },
                };
                var open = new Button { Style = (Style)FindResource("HeaderButton"), Content = tile, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-7, -4, -7, -4), ToolTip = "Play it in this PC's video player" };
                AutomationProperties.SetName(open, $"Play clip {number}");
                open.Click += (_, _) => OpenEvidence(project, new TaskEvidence("", null, shot.Path));
                view.Children.Add(open);
                break;
            default:
                view.Children.Add(PictureView(project, shot.Picture, shot.Path, 560, $"picture {number}"));
                break;
        }
        view.Children.Add(new TextBlock { Text = $"{number}. {shot.Caption}", FontSize = 14.5, Foreground = (Brush)FindResource("TextStrong"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2) });
        view.Children.Add(new TextBlock { Text = $"Taken at: {shot.Settings}", FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap });
        return view;
    }
}
