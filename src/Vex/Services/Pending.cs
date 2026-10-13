using System;
using System.Collections.Generic;
using System.Linq;

namespace Vex.Services;

/// <summary>A nudge or Build it / Restart card that went unanswered, as kept in state.json for the chat's Pending tab.</summary>
public class PendingItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The nudge's type ("stretch", "reminder", "meeting", ...), or "confirm" for a Build it / Restart card.</summary>
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>When it went unanswered.</summary>
    public DateTime At { get; set; }
    /// <summary>The original button labels, in order.</summary>
    public List<string> Options { get; set; } = new();
    /// <summary>What's needed to remake the buttons after a restart (<see cref="Nudge.Args"/>, or a card's action and arg).</summary>
    public Dictionary<string, string> Args { get; set; } = new();
    public DateTime? Until { get; set; }
    /// <summary>A newer item with the same key replaces this one (<see cref="Nudge.PendingKey"/>).</summary>
    public string Key { get; set; } = "";
}

/// <summary>
/// The chat's Pending tab: nudges with options that went without an answer (replaced by another, timed out, hidden when
/// he dozed off or the chat opened) and Build it / Restart cards left behind when the chat closed, so they can still be
/// answered later. Picking an option does what the button would have done then. Kept in state.json; cleared after
/// PendingKeepDays, once a meeting's over, or when the same nudge is answered normally. ✕ on a nudge counts as an answer.
/// </summary>
public static class Pending
{
    /// <summary>The original nudges and cards behind this session's items, so picking one runs the very same action.
    /// After a restart they're gone and the buttons are remade from the item instead.</summary>
    static readonly Dictionary<string, Nudge> liveNudges = new();
    static readonly Dictionary<string, Confirm> liveCards = new();

    /// <summary>Label of the extra button on items with no ✕ of their own: drops the item without doing anything.</summary>
    public const string Drop = "✕";

    /// <summary>Raised when items are added or removed, for the chat's badge.</summary>
    public static event Action? Changed;

    static List<PendingItem> Items => Store.State.Pending;

    /// <summary>What's waiting, newest first.</summary>
    public static List<PendingItem> All { get { Prune(); return Items.OrderByDescending(i => i.At).ToList(); } }

    public static int Count => All.Count;

    /// <summary>A nudge left the screen without a button being pressed. Ones without options, or gone stale, aren't kept.</summary>
    public static void Park(Nudge n)
    {
        if (n.Buttons.Count == 0 || n.StillValid?.Invoke() == false || n.Until < DateTime.Now) return;
        var item = new PendingItem
        {
            Type = n.Type, Text = n.Text, At = DateTime.Now, Options = n.Buttons.Select(b => b.Label).ToList(),
            Args = new(n.Args), Until = n.Until, Key = KeyOf(n),
        };
        Add(item);
        liveNudges[item.Id] = n;
    }

    /// <summary>A Build it / Restart card was still waiting when the chat closed.</summary>
    public static void Park(Confirm c)
    {
        var item = new PendingItem
        {
            Type = "confirm", Text = c.Text, At = DateTime.Now, Options = { c.GoLabel, "No" },
            Args = { ["action"] = c.Action, ["arg"] = c.Arg }, Key = $"confirm|{c.Action}|{c.Arg}",
        };
        Add(item);
        liveCards[item.Id] = c;
    }

    /// <summary>A nudge was answered from its bubble: an older unanswered copy of it (yesterday's stretch, say) is moot.</summary>
    public static void Answered(Nudge n)
    {
        if (Remove(i => i.Key == KeyOf(n))) Save();
    }

    /// <summary>
    /// Picks an option on a waiting item: removes it, then does what the button would have done.
    /// Returns a line for the chat when there's something to report (a build starting, say).
    /// </summary>
    public static string? Choose(string id, string option)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return null;
        liveNudges.TryGetValue(id, out var nudge);
        liveCards.TryGetValue(id, out var card);
        Remove(i => i.Id == id);
        Save();

        if (item.Type == "confirm")
        {
            // Still a click from Ben, so building and restarting stay his call (SPEC section 2, rule 7).
            card ??= Hub.ConfirmFor(item.Args.GetValueOrDefault("action", ""), item.Args.GetValueOrDefault("arg", ""));
            return option == card.GoLabel ? card.Go() : "✗ Left it.";
        }
        nudge ??= Hub.Nudges.Rebuild(item);
        var act = nudge?.Buttons.FirstOrDefault(b => b.Label == option).Act;
        if (act != null) act();
        else if (option != Drop) Store.Log($"Pending: couldn't redo \"{option}\" on a {item.Type} nudge.");
        return null;
    }

    /// <summary>Clears out items past PendingKeepDays, meetings that have ended and nudges that went stale (Claude was answered).</summary>
    public static void Prune()
    {
        var cutoff = DateTime.Now.AddDays(-Store.Config.PendingKeepDays);
        if (Remove(i => i.At < cutoff || i.Until < DateTime.Now
                        || liveNudges.TryGetValue(i.Id, out var n) && n.StillValid?.Invoke() == false))
            Save();
    }

    static string KeyOf(Nudge n) => n.PendingKey ?? $"{n.Type}|{n.Text}";

    static void Add(PendingItem item)
    {
        Remove(i => i.Key == item.Key);
        Items.Add(item);
        Save();
    }

    static bool Remove(Func<PendingItem, bool> match)
    {
        var gone = Items.Where(match).ToList();
        foreach (var i in gone) { Items.Remove(i); liveNudges.Remove(i.Id); liveCards.Remove(i.Id); }
        return gone.Count > 0;
    }

    static void Save()
    {
        Store.SaveState();
        Changed?.Invoke();
    }
}
