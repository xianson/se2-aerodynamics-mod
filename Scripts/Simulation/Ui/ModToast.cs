using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.GameSystems.UserMessages;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// A TOAST WITH LITERAL TEXT (2026-10-20). The game's UserMessage takes its title and its content as localization KEYS (LocKey):
/// UserMessagesSystemSessionComponent.DisplayMessageImpl evaluates TextManager.GetText(TitleId, TitleParameters) and
/// GetText(ContentId, ContentParameters) - a mod's sentence passed as the key logged two "LOCALIZATION WARNING: Could not find key
/// ..." lines per toast (LocalizationPackage.GetText, then the key's own text shown), and a '{' or '}' in it went through the
/// format evaluation (TextManager.EvaluateTextUsingFilter: a warning, or an exception on a lone brace). The game's own keys for
/// its user-message fanfare are a bare parameter in every culture - "UserMessageFanfareTitle": "{Title}",
/// "UserMessageFanfareContent": "{Content}" (Vanilla Texts/*/Dialogs.loc-texts, all 13 cultures) - and a parameter's value is
/// appended as it is (FormatParamsFilter: StringBuilder.Append, no evaluation). So the text goes in as the parameter: shown
/// exactly, braces and all, nothing logged. Every toast both mods show goes through here (Tests: the code scan for
/// LocKey.FromString on a UserMessage's TitleId / ContentId).
/// </summary>
public static class ModToast
{
    public const string TitleKey = "UserMessageFanfareTitle", ContentKey = "UserMessageFanfareContent";

    /// <summary>A game toast (UserMessageType.Game) showing title and text as written; messageKey (optional): a new toast with the
    /// same key replaces the last.</summary>
    public static UserMessage Literal(string title, string text, double seconds, string messageKey = null)
    {
        var m = new UserMessage
        {
            TitleId = LocKey.FromString(TitleKey),
            TitleParameters = new Dictionary<string, object> { ["Title"] = title ?? "" },
            ContentId = LocKey.FromString(ContentKey),
            ContentParameters = new Dictionary<string, object> { ["Content"] = text ?? "" },
            Type = UserMessageType.Game,
            TextTimeout = TimeSpan.FromSeconds(seconds),
        };
        if (messageKey != null) m.MessageKey = StringId.Get(messageKey);
        return m;
    }
}
