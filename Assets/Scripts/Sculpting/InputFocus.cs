using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sculpting
{
    /// Keyboard-focus questions every hotkey handler has to ask before treating a key press as a
    /// shortcut. One copy, so every tool answers them the same way.
    internal static class InputFocus
    {
        /// True while a uGUI text field has keyboard focus (renaming an object, typing a value) -
        /// the key being pressed is text for that field, not a shortcut.
        public static bool IsTypingInText()
        {
            EventSystem es = EventSystem.current;
            GameObject focused = es != null ? es.currentSelectedGameObject : null;
            if (focused == null) return false;
            var field = focused.GetComponent<InputField>();
            return field != null && field.isFocused;
        }
    }
}
