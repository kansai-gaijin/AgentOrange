namespace AgentOrange;

/// <summary>T3 Code's diff panel, on the right of the conversation. Being copied from T3's code next.</summary>
public sealed partial class AgentsWindow
{
    private void ShowDiffPanel(AgentConversation conversation, AgentItem reply, string? path) =>
        Toast("Diff panel", "The full diff opens here once T3's diff panel is copied.", error: false);
}
