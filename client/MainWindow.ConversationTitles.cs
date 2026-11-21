using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrange;

/// <summary>
/// Conversations' titles (#125). A conversation is first named by the start of its first message,
/// and a project's conversations often start with the same words, so once its first turn has ended
/// its own login names it from what it's about, in a few words, as T3 Code titles a thread (its
/// prompt, adapted). The person can rename it, and nothing renames it after that. What picks a
/// conversation by its title refuses one that more than one has, naming them.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<string> _titlesAsked = [];

    // Once a turn has ended with a reply, for a conversation still named by its first message. Once a
    // run of the app at most, so one whose login couldn't answer is asked again after a restart. Only
    // a turn that ended in this run counts, so conversations read as the app starts aren't all asked at once.
    private void WriteTitleIfDue(AgentConversation conversation)
    {
        if (conversation.TitleFrom is not null || conversation.Settled || conversation.IsWorking || conversation.IsWaiting || conversation.TurnEndedAt == default) return;
        if (!conversation.Items.Any(item => item.Kind == AgentItemKind.Assistant && item.Text.Trim().Length > 0)) return;
        if (!_titlesAsked.Add(conversation.Id)) return;
        _ = WriteTitleAsync(conversation);
    }

    private async Task WriteTitleAsync(AgentConversation conversation)
    {
        var (answer, problem) = await AskForTitleAsync(conversation, TitlePrompt(conversation));
        if (TitleIn(answer) is not { } title)
        {
            AppLog.Write($"A conversation's title couldn't be written, so it keeps the start of its first message: {problem ?? "its login gave no title"}");
            return;
        }
        if (conversation.TitleFrom is not null) return; // Renamed meanwhile.
        conversation.Title = title;
        conversation.TitleFrom = AgentConversation.TitleWritten;
        ConversationChanged(conversation);
        _agentsWindow?.ShowHeaderFor(conversation);
    }

    private async Task<(string? Text, string? Problem)> AskForTitleAsync(AgentConversation conversation, string prompt)
    {
#if DEBUG
        // Checks' logins are scripts that only play a turn, so only titleanswer names a conversation there.
        if (Headless.Active) return (_titleAnswerForChecks, _titleAnswerForChecks is null ? "checks have no real login to ask" : null);
#endif
        if (AgentProviderFor(conversation) is not { } login) return (null, "it has no login to ask");
        if ((conversation.WorkFolder ?? ProjectFolder(conversation.ProjectId)) is not { } folder) return (null, "its project has no folder on this PC");
        return await AgentText.AskAsync(login, prompt, folder);
    }

    // T3 Code's prompt for a thread's first title, for a conversation about a game's project: its
    // first message and its first reply, as the reply says what it took on.
    private string TitlePrompt(AgentConversation conversation)
    {
        static string Limit(string text, int most) => text.Length > most ? text[..most] + "\n[The rest is left out]" : text;
        var asked = conversation.Items.FirstOrDefault(item => item.Kind == AgentItemKind.User && !item.Automatic)?.Text
            ?? conversation.Items.FirstOrDefault(item => item.Kind == AgentItemKind.User)?.Text ?? "";
        var replied = string.Join("\n\n", conversation.Items.Where(item => item.Kind == AgentItemKind.Assistant && item.Text.Trim().Length > 0).Take(3).Select(item => item.Text.Trim()));
        var project = ProjectName(conversation.ProjectId);
        return $$"""
            Generate a title that will help the person recognize this Agent Orange conversation weeks later, among many others about the same project that often start with the same words.
            Return JSON with the key title, as {"title": "..."}.

            Before answering, silently reduce the conversation to:
            - Subject: What part of the game, feature, or problem is this really about?
            - Outcome: What does the person ultimately want made, understood, or changed?
            - Incidental instructions: What only describes how the agent should do the work?

            Title the subject and outcome. Discard incidental instructions.

            Editorial rules:
            - 3-8 words, fewer than 50 characters.
            - Use a compact noun phrase or clear action phrase.
            - When it works on a milestone, slices or tasks, name them (such as "Priority 9 · orbits and flight limits"), as that is what tells it apart from the others.
            - Capture the umbrella goal when the request lists several symptoms or steps.
            - Name the change to the game or app, not the plan, report, branch, or commit used to make it.
            - Models, subagents, tools, output formats, and monitoring instructions do not belong in the title unless they are themselves the topic.
            - Do not claim the work is complete.
            - Do not copy and truncate the person's message.
            - Never include the project name ({{JsonSerializer.Serialize(project)}}); the window already shows it next to the title. Avoid quotes, labels, filler, and trailing punctuation.

            The person's first message:
            {{Limit(asked, 8000)}}

            The agent's first reply:
            {{Limit(replied, 4000)}}
            """;
    }

    /// <summary>A title from a login's answer, as T3 cleans one: its JSON's title or the first line, without quotes, one line, not too long. Null when there's none.</summary>
    internal static string? TitleIn(string? answer)
    {
        if (answer is null) return null;
        var title = AgentText.JsonIn(answer) is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("title", out var given) && given.ValueKind == JsonValueKind.String
            ? given.GetString() ?? ""
            : answer.Trim().Split('\n')[0];
        title = Regex.Replace(title.Trim(), "^(['\"`])(.*)\\1$", "$2").Trim();
        title = Regex.Replace(title, @"\s+", " ").TrimEnd('.', ' ');
        if (title.Length == 0 || title.Equals("title", StringComparison.OrdinalIgnoreCase)) return null;
        return title.Length > AgentConversation.MostTitle ? title[..(AgentConversation.MostTitle - 1)].TrimEnd() + "…" : title;
    }

    // Titles that clash

    /// <summary>The titles more than one listed conversation has, as <see cref="TitleKey"/> gives them.</summary>
    internal HashSet<string> ClashingTitles() =>
        Conversations.Where(each => each.Items.Count > 0 && each.Title.Length > 0)
            .GroupBy(each => TitleKey(each.Title), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A title as titles are compared: one line, whatever case.</summary>
    internal static string TitleKey(string title) => Regex.Replace(title, @"\s+", " ").Trim();

    /// <summary>Another listed conversation has the same title, whatever case.</summary>
    internal bool TitleClashes(AgentConversation conversation) => ClashingTitles().Contains(TitleKey(conversation.Title));

    private static bool SameTitle(string one, string other) => string.Equals(TitleKey(one), TitleKey(other), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What tells a conversation apart from others with its title: the start of its id, first as a
    /// narrow row cuts the end off, and when it was last active, as its list row shows them.
    /// </summary>
    internal static string TellsApart(AgentConversation conversation) =>
        $"{ShortConversationId(conversation)} · {conversation.Updated.ToLocalTime():d MMM HH:mm}";

    internal static string ShortConversationId(AgentConversation conversation) => conversation.Id.Length > 6 ? conversation.Id[..6] : conversation.Id;

    /// <summary>
    /// The conversation with that title, for whatever picks one by its title. More than one with it
    /// is refused, naming each with what tells it apart; the start of its id picks one of them.
    /// </summary>
    internal AgentConversation ConversationTitled(string title)
    {
        var all = Conversations.Where(each => each.Items.Count > 0 || each.Title.Length > 0).ToList();
        var named = all.Where(each => SameTitle(each.Title, title)).ToList();
        if (named.Count == 1) return named[0];
        if (named.Count > 1)
        {
            throw new InvalidOperationException($"{named.Count} conversations are called {title}, so it's not clear which: "
                + string.Join("; ", named.OrderByDescending(each => each.Updated).Select(each => $"\"{each.Title}\" ({TellsApart(each)})"))
                + ". Name one by the start of its id.");
        }
        // By the start of its id, as the clash above names them.
        var byId = all.Where(each => title.Length >= 4 && each.Id.StartsWith(title, StringComparison.OrdinalIgnoreCase)).ToList();
        return byId.Count == 1 ? byId[0] : throw new InvalidOperationException($"There's no conversation called {title}.");
    }

#if DEBUG
    // What a login answers when asked for a title, in headless checks, which have no real login.
    private string? _titleAnswerForChecks;

    /// <summary>Headless checks only: what a login answers when asked for a conversation's title.</summary>
    internal void SetTitleAnswerForChecks(string answer) => _titleAnswerForChecks = answer;

    /// <summary>Headless checks only: the check's conversation is renamed, as the person renames it.</summary>
    internal void RenameForChecks(string title) => CheckConversation.Rename(title);

    /// <summary>Headless checks only: the check's conversation's row becomes a box to rename it in, as F2 or a double-click does it.</summary>
    internal void StartRenameForChecks() => (_agentsWindow ?? throw new InvalidOperationException("Open the agents window first.")).StartRename(CheckConversation);

    /// <summary>Headless checks only: each conversation's title, how it came about, and what tells it apart.</summary>
    internal string TitlesForChecks() =>
        string.Join("\n", Conversations.Where(each => each.Items.Count > 0).Select(each => $"{each.Title} | {each.TitleFrom ?? "first message"}{(TitleClashes(each) ? $" | clashes, told apart by {TellsApart(each)}" : "")}"));
#endif
}
