using System.IO;

namespace AgentOrange;

/// <summary>
/// Going on with another login when a conversation's runs out (#109), when the person has turned it on
/// (Settings › Providers). Another login of the same kind, with room left in its subscription's limits,
/// takes the conversation on straight away: its session is carried over when it can be, and otherwise
/// the handoff note Agent Orange wrote as it stopped goes with the message, so the new session starts
/// from that rather than reading everything again. Paid extra usage never counts as room, and a login
/// that goes on to it mid-turn is stopped and moved to one with room instead.
/// </summary>
public partial class MainWindow
{
    internal const string SwitchLoginSetting = "agents_switch_login_at_limit";

    // What a login needs left of each of its limits to count as having room.
    private const double RoomLeft = 5;

    private static readonly TimeSpan FreshUsage = TimeSpan.FromMinutes(2);

    private readonly HashSet<string> _switchingLogin = [];

    internal bool SwitchesLoginAtLimit => _database.GetSetting(SwitchLoginSetting) == "1";

    /// <summary>
    /// Its subscription's limits are known and none is used up or nearly: a login whose limits aren't
    /// known could have only paid extra usage left, so it doesn't count.
    /// </summary>
    internal bool HasRoom(AgentProvider login)
    {
        var limits = Usage.For(login.Id).Where(limit => limit.Kind != "overage").ToList();
        if (limits.Count == 0 || Usage.NotSignedIn(login.Id) is not null) return false;

        // A limit that's reset since it was last heard has room again.
        static bool Full(AgentLimit limit) => (limit.Blocked || limit.Used >= 100 - RoomLeft) && !(limit.ResetsAt is { } resets && resets <= DateTimeOffset.UtcNow);
        return !limits.Any(Full);
    }

    // The others of its kind, turned on, with the one with the most room first.
    private List<AgentProvider> OtherLogins(AgentConversation conversation, AgentProvider from) =>
        [.. AgentLogins(conversation).Where(login => login.Id != from.Id && login.Kind == from.Kind)];

    private AgentProvider? MostRoom(IEnumerable<AgentProvider> logins) =>
        logins.Where(HasRoom).OrderBy(login => Usage.For(login.Id).Where(limit => limit.Kind != "overage").Select(limit => limit.Used ?? 0).DefaultIfEmpty(0).Max()).FirstOrDefault();

    /// <summary>The login it would go on with now, from what's known of each login's limits, or null for none.</summary>
    internal AgentProvider? LoginToGoOnWith(AgentConversation conversation) =>
        AgentProviderFor(conversation) is { } from ? MostRoom(OtherLogins(conversation, from)) : null;

    // Its login went on to paid extra usage mid-turn: stopped, to go on with one that has room, if there is one.
    private void ExtraUsageStarted(AgentConversation conversation, AgentLimit limit)
    {
        if (!SwitchesLoginAtLimit || !conversation.ResumeAtReset || LoginToGoOnWith(conversation) is null) return;
        conversation.StopAtLimit(limit.ResetsAt);
    }

    /// <summary>
    /// A limit stopped it: goes on with the other login of its kind that has the most room, after asking
    /// any whose limits weren't heard in the last two minutes. Says so in the conversation when none has room.
    /// </summary>
    internal async Task GoOnWithAnotherLoginAsync(AgentConversation conversation)
    {
        if (!_switchingLogin.Add(conversation.Id)) return;
        try
        {
            if (AgentProviderFor(conversation) is not { } from) return;
            var others = OtherLogins(conversation, from);
            if (others.Count == 0) return;
            var folder = Path.Combine(Settings.Folders.AgentFolder, "usage-check");
            foreach (var login in others)
            {
                if (Usage.HeardAt(login.Id) is { } heard && DateTimeOffset.UtcNow - heard < FreshUsage && !Usage.AsLastHeard(login.Id)) continue;
                if (_conversations?.Any(each => each.IsWorking && each.ProviderId == login.Id) == true) continue; // It says its limits as it works.
                await Usage.ProbeAsync(login, folder);
            }

            // The person may have gone on with it some other way meanwhile.
            if (!conversation.IsLimited || conversation.IsWorking || conversation.Settled) return;
            if (MostRoom(others) is not { } to)
            {
                NoteOnce(conversation, $"There's room left in the limits of no other {AgentProviders.KindName(from.Kind)} login, so it waits for {from.Label}'s limit to reset.");
                return;
            }
            if (await AgentProblemAsync(conversation) is { } problem)
            {
                NoteOnce(conversation, $"Couldn't go on with {to.Label}: {problem}");
                return;
            }

            var note = LimitHandoffText(conversation); // Before switching, as it says when the old login's limit resets.
            conversation.SwitchProvider(from, to);
            var carried = !conversation.NeedsHandoff;
            conversation.NeedsHandoff = false; // The note goes instead of everything said so far.
            conversation.Send(GoOnMessage(from, to, carried, note), [], ViewLabel(), DescribeView(), () => PrepareAgent(conversation), automatic: true);
        }
        finally
        {
            _switchingLogin.Remove(conversation.Id);
        }
    }

    // Said once, not again each time it's looked at.
    private static void NoteOnce(AgentConversation conversation, string text)
    {
        if (conversation.Items.LastOrDefault() is { Kind: AgentItemKind.Notice } last && last.Text == text) return;
        conversation.Note(text);
    }

    /// <summary>What it's sent as it goes on with the other login: just to go on when its session came along, or else the handoff note.</summary>
    internal static string GoOnMessage(AgentProvider from, AgentProvider to, bool carried, string note) => carried
        ? $"{from.Label} ran out of usage, so this conversation goes on with {to.Label}. Continue where you left off."
        : $"{from.Label} ran out of usage, so this conversation goes on with {to.Label}, whose agent program wasn't there for it. "
            + $"Start from the handoff note Agent Orange wrote as it stopped, rather than reading everything again:\n\n{note}\n\nContinue where you left off.";
}
