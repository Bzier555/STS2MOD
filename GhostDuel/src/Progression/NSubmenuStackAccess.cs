using System.Reflection;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace GhostDuel.Progression;

/// <summary>
/// <c>NSubmenu._stack</c> (confirmed: <c>NSubmenu.cs:108</c>, <c>protected NSubmenuStack _stack;</c>)
/// is how every submenu screen (including <c>NSingleplayerSubmenu</c>) navigates to another screen —
/// e.g. <c>NSingleplayerSubmenu.OpenCharacterSelect</c> does <c>_stack.GetSubmenuType&lt;
/// NCharacterSelectScreen&gt;(); _stack.Push(submenuType);</c>. <c>GetSubmenuType&lt;T&gt;()</c> and
/// <c>Push(NSubmenu)</c> are both public (<c>NSubmenuStack.cs:112,118</c>) — only the field holding the
/// stack reference itself is protected, and our menu-button code lives outside this class hierarchy
/// (it isn't a subclass, just a Harmony postfix on <c>NSingleplayerSubmenu._Ready</c>), so this one
/// field read is the only reflection needed to reuse the exact same navigation the native Standard/
/// Daily/Custom buttons already use. One file, one reflected name, load-time existence assertion,
/// matching this project's established accessor pattern.
/// </summary>
internal static class NSubmenuStackAccess
{
    private static readonly FieldInfo StackField =
        typeof(NSubmenu).GetField("_stack", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NSubmenu).FullName, "_stack");

    public static NSubmenuStack GetStack(NSubmenu submenu) =>
        (NSubmenuStack)StackField.GetValue(submenu)!;
}
