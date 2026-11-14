using System.Windows.Input;

namespace AgentOrange;

/// <summary>
/// Which of Shift, Ctrl and Alt are held. The keyboard's own answer, except while the dev build's
/// drive tool sends a shortcut such as Shift+A, when it's the keys that shortcut holds.
/// </summary>
internal static class KeyState
{
#if DEBUG
    internal static ModifierKeys? Held;

    public static ModifierKeys Modifiers => Held ?? Keyboard.Modifiers;
#else
    public static ModifierKeys Modifiers => Keyboard.Modifiers;
#endif
}
