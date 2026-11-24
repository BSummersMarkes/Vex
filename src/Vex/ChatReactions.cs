using System;
using System.Linq;

namespace Vex;

/// <summary>
/// The fixed set of reactions Vex can put on one of your chat messages, the way people do in Teams: like, love, laugh
/// and sad. The brain picks one with [[react: …]] (Hub.ActionHelp); the chat draws it as a small badge on your newest
/// message and keeps it in the day's transcript as a "react" line, so it's still there when the chat reopens. Anything
/// outside the set is ignored, so there's never an emoji the chat can't show.
/// </summary>
static class ChatReactions
{
    /// <summary>Id (what's stored and what the brain writes), the glyph shown, and other words the brain might use for it.</summary>
    public static readonly (string Id, string Glyph, string[] Also)[] All =
    {
        ("like", "👍", new[] { "thumbs up", "thumbsup", "thumbs-up", "+1", "👍" }),
        ("love", "❤", new[] { "heart", "❤️", "❤" }),
        ("laugh", "😂", new[] { "laughing", "haha", "lol", "funny", "😂" }),
        ("sad", "😢", new[] { "crying", "cry", "tear", "😢" }),
    };

    /// <summary>The reaction's id for whatever the brain wrote ("heart", "❤️", "Love"), or null if it isn't one of the four.</summary>
    public static string? Parse(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return All.FirstOrDefault(r => r.Id == t || r.Also.Contains(t)).Id;
    }

    public static string Glyph(string id) => All.FirstOrDefault(r => r.Id == id).Glyph ?? "";

    /// <summary>How a reaction reads in plain-text copies of the chat (the copy button, the recap, the diary).</summary>
    public static string Describe(string id) => $"(Vex reacted: {id})";
}
