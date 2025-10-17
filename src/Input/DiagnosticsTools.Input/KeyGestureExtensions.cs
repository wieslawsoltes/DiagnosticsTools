using Avalonia.Input;
using Avalonia.Input.Raw;

namespace Avalonia.Diagnostics;

/// <summary>
/// Provides helpers for matching raw key events to configured key gestures.
/// </summary>
public static class KeyGestureExtensions
{
    /// <summary>
    /// Determines whether the supplied raw key event matches the gesture.
    /// </summary>
    public static bool Matches(this KeyGesture gesture, RawKeyEventArgs keyEvent)
    {
        var modifiers = (KeyModifiers)(keyEvent.Modifiers & RawInputModifiers.KeyboardMask);
        modifiers = MergeModifierKey(keyEvent.Key, modifiers);

        if (gesture.Key != Key.None &&
            ResolveNumPadOperationKey(keyEvent.Key) != ResolveNumPadOperationKey(gesture.Key))
        {
            return false;
        }

        return (modifiers & gesture.KeyModifiers) == gesture.KeyModifiers;
    }

    private static Key ResolveNumPadOperationKey(Key key) =>
        key switch
        {
            Key.Add => Key.OemPlus,
            Key.Subtract => Key.OemMinus,
            Key.Decimal => Key.OemPeriod,
            _ => key,
        };

    private static KeyModifiers MergeModifierKey(Key key, KeyModifiers modifiers) =>
        key switch
        {
            Key.LeftCtrl or Key.RightCtrl => modifiers | KeyModifiers.Control,
            Key.LeftShift or Key.RightShift => modifiers | KeyModifiers.Shift,
            Key.LeftAlt or Key.RightAlt => modifiers | KeyModifiers.Alt,
            Key.LWin or Key.RWin => modifiers | KeyModifiers.Meta,
            _ => modifiers
        };
}
