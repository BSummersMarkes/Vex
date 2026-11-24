using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace Vex;

/// <summary>Whether he can do a skill on request ([[show: …]] in chat), and if so whether it needs the chat closed first.</summary>
enum Cue
{
    /// <summary>Happens only for real (a nudge, lunch, being dragged…): described, never performed on request.</summary>
    No,
    /// <summary>Done there and then, below the open chat (it's all on his own figure).</summary>
    Now,
    /// <summary>Needs the chat out of the way (his daemon hides from it, climbing and napping stop for it): waits for it to close.</summary>
    AfterChat,
}

/// <summary>
/// One thing Vex does on screen, as he knows it about himself: an id (what [[show: id]] takes), a short name, what it
/// looks like and when it normally happens. Perform starts it on request and returns why not if it can't; On says
/// whether it's switched on in Setup; Blocked says why it can't be shown at all right now (its setting is off);
/// Instead says how to see one that can't be shown on cue. Text is written to Vex ("you"); "@who" becomes the user's name.
/// </summary>
record Skill(string Id, string Name, string What, string When, Cue Cue = Cue.No, Func<VexWindow, string?>? Perform = null,
             Func<bool>? On = null, Func<string?>? Blocked = null, string Instead = "");

/// <summary>
/// Vex knowing his own animations and being able to show them off. <see cref="Skills"/> is the single list: the chat
/// context's "Things you do" (<see cref="SkillsSummary"/>, so he can answer "what do you do when you're bored?") and
/// the [[show: …]] action (<see cref="Show"/>, so "go on, juggle" works) both read it. A new animation or behaviour
/// needs a row here; an idle activity added to IdleActs.All without one still gets listed (by its Setup name), and the
/// seasonal outfits are read straight from Occasions.All, so the list can't quietly fall behind.
///
/// On request, the idle activities play even if switched off in Setup (those switches are about what he does by
/// himself; being asked is different). The daemon and the laptop do need their settings on, since those switch the
/// whole thing off. Anything that needs the chat closed waits for it (ShowWaitSeconds at most), then goes ahead once
/// he's standing about with no nudge, bubble, call or full screen in the way. Full Vex only: Lite has no chat.
/// </summary>
public partial class VexWindow
{
    internal static readonly Skill[] Skills =
    {
        // Always going on.
        new("idle", "Standing about",
            "you breathe, blink, and turn your head to follow @who's mouse pointer when it's near; you hold still while it's over you",
            "all the time when nothing else is on"),
        new("wander", "Wandering",
            "you stroll a little way along the taskbar, or just look the other way",
            "every half a minute or so while idle; not while the chat is open, a nudge is waiting or your daemon's out"),
        new("dodge", "Dodging",
            "you walk out from under the window @who is working in, across to the other monitor on foot if the taskbars join; with nowhere clear you just stand still",
            "whenever the window in front covers you"),
        new("vanish", "Vanishing",
            "you disappear completely, and you never show up in screen shares or screenshots",
            "a full-screen presentation or video with nowhere clear to stand, or Hide in Setup",
            Instead: "It's not much to watch."),
        new("fall", "Being dropped",
            "picked up by the mouse and let go, you fall to the floor and land mildly annoyed",
            "when @who drags and drops you",
            Instead: "@who can pick you up and drop you to see it."),
        new("perch", "Perching",
            "you stand on top of a window, ride along when it moves and stroll along it; if it closes, moves or gets covered you drop down and land with a flat, unimpressed look",
            "when @who drops you on a window's top edge, or after climbing one",
            Instead: "@who can drop you on top of a window, or ask you to climb."),

        // The idle activities: one picked at random now and then while you're standing about (VexActs.cs).
        new("sip", "Sip coffee",
            "the mug up to your face, tipped, eyes shut, a little \"ahh\"",
            "the commonest idle pick, while there's coffee left in the mug and not more than about once a minute",
            Cue.Now, v => v.ShowIdle(Act.Sip), On: () => IdleActs.Enabled(Act.Sip)),
        new("phone", "Check your phone",
            "you look down at your phone and scroll now and then",
            "an idle pick; also while sitting against the screen edge",
            Cue.Now, v => v.ShowIdle(Act.Phone), On: () => IdleActs.Enabled(Act.Phone)),
        new("read", "Read a book",
            "you read a little book, turning a page every few seconds",
            "an idle pick; also while sitting against the screen edge, and instead of sitting when the edge is covered or you're on a window",
            Cue.Now, v => v.ShowIdle(Act.Read), On: () => IdleActs.Enabled(Act.Read)),
        new("sit", "Sit against the screen edge",
            "you walk to the nearer edge of the screen and sit leaning back against it for a minute or so, reading, on your phone or just looking about",
            "an idle pick",
            Cue.Now, v => v.ShowIdle(Act.Sit), On: () => IdleActs.Enabled(Act.Sit)),
        new("stretch", "Stretch",
            "mittens up high, eyes squeezed shut, standing a little taller",
            "an idle pick, much likelier once @who has been sitting for 45 minutes or more (a hint)",
            Cue.Now, v => v.ShowIdle(Act.Stretch), On: () => IdleActs.Enabled(Act.Stretch)),
        new("watch", "Check your watch",
            "you lift your wrist and look at your watch",
            "only when a meeting starts within 15 minutes, every few minutes until then (a hint)",
            Cue.Now, v => v.ShowIdle(Act.Watch), On: () => IdleActs.Enabled(Act.Watch)),
        new("juggle", "Juggle",
            "you put your mug down, juggle three balls for a few seconds with your eyes on the highest, then pocket them and pick the mug up; now and then one gets away and rolls off, and you give it a flat look and a sigh before fetching it",
            "a rare idle pick, a bit of showing off",
            Cue.Now, v => v.ShowIdle(Act.Juggle), On: () => IdleActs.Enabled(Act.Juggle)),
        new("climb", "Climb windows",
            "you walk to a window's side and climb it hand over hand, mug hooked on your back, and pull yourself onto the top; from a window top, back down a side again",
            "an idle pick, only when there's a clear window edge within reach; you come straight down for the chat, a bubble or a click",
            Cue.AfterChat, v => v.ShowClimb(), On: () => IdleActs.Enabled(Act.Climb)),

        // His daemon (VexDaemon.cs).
        new("daemon", "Your daemon",
            "a small cat drawn like you lives in your hoodie; it pops out of your pocket, looks up at you, plays (one of the ones below), then hops back in. Sometimes it shapeshifts into a mouse, a moth or a little bird and back",
            "now and then when you're bored and @who is at the desk (rarely, sometimes or often, set in Setup); it vanishes in a blink for bubbles, the chat, calls and full screen, and curls up asleep beside you when you nap or have lunch",
            Cue.AfterChat, v => v.ShowDaemon(null, null), On: () => Store.Config.Daemon, Blocked: DaemonOff),
        new("daemon run", "Daemon: a run", "it dashes along the taskbar and back", "one of its routines",
            Cue.AfterChat, v => v.ShowDaemon("run", false), On: () => Store.Config.Daemon, Blocked: DaemonOff),
        new("daemon shoulder", "Daemon: on your shoulder", "it hops up onto your shoulder and looks about for a while", "one of its routines",
            Cue.AfterChat, v => v.ShowDaemon("shoulder", null), On: () => Store.Config.Daemon, Blocked: DaemonOff),
        new("daemon play", "Daemon: chasing a mote", "it chases a small glowing mote that flits about beside you, missing a couple of times before catching it", "one of its routines",
            Cue.AfterChat, v => v.ShowDaemon("play", null), On: () => Store.Config.Daemon, Blocked: DaemonOff),
        new("daemon curl", "Daemon: a curl-up", "it curls up beside you for a few seconds, then stretches", "one of its routines",
            Cue.AfterChat, v => v.ShowDaemon("curl", false), On: () => Store.Config.Daemon, Blocked: DaemonOff),
        new("daemon pounce", "Daemon: pouncing", "it stalks and pounces on your boots and bats at them; you flinch and look down", "one of its routines",
            Cue.AfterChat, v => v.ShowDaemon("pounce", false), On: () => Store.Config.Daemon, Blocked: DaemonOff),
        new("daemon shapeshift", "Daemon: shapeshifting", "it runs off along the taskbar as a cat and comes back as a mouse, moth or bird (sometimes all three in turn), with a shrink, a soft puff and a grow at each change", "part of a visit, now and then",
            Cue.AfterChat, v => v.ShowDaemon("run", true), On: () => Store.Config.Daemon, Blocked: DaemonOff),

        // Rest and routine.
        new("nap", "Doze off",
            "you sit and doze, eyes shut, with a Zzz",
            "after a few minutes with no keyboard or mouse; you wake when @who is back (or when asked: Have a nap in Setup)",
            Cue.AfterChat, v => v.ShowNap()),
        new("lunch", "Lunch",
            "you walk to a bottom corner, put your mug down, sit and eat a sandwich from a lunch box, then nap for the rest of the break with a countdown clock beside you; a flinch and a dry line when time's up, a groggy wake if @who is back early",
            "only when @who goes for lunch (the lunch nudge's Going, [[break: going to lunch]], or Setup)",
            On: () => Store.Config.LunchRoutine, Instead: "It's for a real lunch break: [[break: going to lunch]] when @who is going."),
        new("laptop", "Coding at the laptop",
            "you sit in a bottom corner and type on a small laptop whose screen lights your face, pausing to think; then you shut the lid, stand, and nod (it worked) or shrug (it didn't)",
            "while your workshop is building a change to you, and (if switched on) while a Claude Code session is working",
            Cue.AfterChat, v => v.ShowLaptop(), On: () => Store.Config.CodingAtLaptop,
            Blocked: () => Store.Config.CodingAtLaptop ? null : "The laptop's switched off in Setup → Behaviour."),
        new("knock", "Knocking on the glass",
            "you step up and knock on the screen: three raps of your mitten with a ripple, and a soft knock sound unless @who is in a call",
            "when a nudge needs @who; again every 90 seconds, up to three times, if it's not answered",
            Cue.Now, v => v.ShowKnock()),
        new("breakclock", "Break clock",
            "a small countdown beside your hood, then a soft chime",
            "during the 20-second look-away and during lunch",
            On: () => Store.Config.BreakClock, Instead: "[[break: eyes]] starts the look-away for real."),

        // The mug (section 5 of the spec).
        new("mug", "The mug",
            "your coffee empties over time and stops steaming when it's cold or empty; when @who clicks it you refill yours and remark on how many that makes today",
            "always; refilled when @who says they've refilled theirs",
            Instead: "@who clicks the mug to refill it."),

        // Dressing up and occasions (VexOutfits.cs, VexBirthday.cs).
        new("outfits", "Seasonal outfits",
            "you wear " + string.Join(", ", Occasions.All.Select(o => $"{Words(o.Outfit)} for {o.Name}")) +
            " from a few days before to the day itself, with one dry remark a year; and a party hat on @who's birthday",
            "by the calendar",
            On: () => Store.Config.DressUp, Instead: "@who can try one on in Setup → Theme."),
        new("confetti", "Party popper",
            "you fire a party popper from your free mitten: confetti and curly streamers over the desktop around you, with a pop",
            "with your happy-birthday line on @who's birthday",
            Cue.AfterChat, v => v.ShowPopper()),
    };

    static string? DaemonOff() => Store.Config.Daemon ? null : "My daemon's switched off in Setup → Behaviour.";

    /// <summary>An outfit's drawing name as words: "SantaHat" → "a santa hat", "OniMask" → "an oni mask".</summary>
    static string Words(string name)
    {
        var w = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        if (w.EndsWith("s")) return w;   // "bunny ears" rather than "a bunny ears"
        return ("aeiou".Contains(w[0]) ? "an " : "a ") + w;
    }

    /// <summary>
    /// The "Things you do" part of the chat context: every skill with what it looks like, when it happens, whether it's
    /// switched off, and whether he can show it on request. Reads only the table and config, so it's safe off the UI thread.
    /// </summary>
    internal static string SkillsSummary(string who)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Things you do on screen (your own animations and habits; use this when asked what you do or get up to. " +
                      "Those marked \"on cue\" you can perform with [[show: id]] when asked):");
        foreach (var s in Skills)
        {
            var off = s.On is { } on && !on() ? " Switched off in Setup, so you don't do it by yourself." : "";
            var cue = s.Cue switch
            {
                Cue.Now => " On cue.",
                Cue.AfterChat => " On cue, once the chat is closed.",
                _ => "",
            };
            sb.AppendLine($"- {s.Id} ({s.Name}): {s.What}. When: {s.When}.{off}{cue}".Replace("@who", who));
        }
        // Idle activities added since this list was last written still show up, by their Setup name.
        foreach (var a in IdleActs.All.Where(a => Skills.All(s => s.Id != a.Id)))
            sb.AppendLine($"- {a.Id} ({a.Name}): one of your idle activities, picked now and then when you're bored.");
        return sb.ToString();
    }

    /// <summary>The ids that [[show: …]] takes, for the action help.</summary>
    internal static string ShowableIds => string.Join(", ", Skills.Where(s => s.Cue != Cue.No).Select(s => s.Id));

    Skill? cued;            // asked for, waiting for the chat to close (or for him to be free)
    double cuedUntil;       // when it's given up on (ShowWaitSeconds)
    double showNapUntil = -1;   // a nap that's only for show ends here

    /// <summary>
    /// [[show: …]] from the chat: do one of his skills now, or as soon as it can be seen. Returns the chat's ✓/✗ line.
    /// Matches the id or the name, ignoring case, dashes and underscores ("daemon-run", "Juggle").
    /// </summary>
    public string Show(string name)
    {
        var want = Regex.Replace(name.Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' ').Replace(':', ' '), @"\s+", " ");
        var s = Skills.FirstOrDefault(k => k.Id == want || k.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (s == null) return $"✗ I don't do anything called \"{name.Trim()}\".";
        if (s.Cue == Cue.No) return $"✗ {s.Name} isn't something I do on cue.{(s.Instead.Length > 0 ? " " + s.Instead.Replace("@who", Services.Hub.Who) : "")}";
        if (s.Blocked?.Invoke() is { } blocked) return "✗ " + blocked;
        if (AtLunch) return "✗ Not while I'm on my lunch break.";
        if (Coding) return "✗ Not while I'm at the laptop.";

        // Things on his own figure can start right now: stop strolling, or wake up, to do them.
        if (s.Cue == Cue.Now)
        {
            if (mode == Mode.Walking && pendingAct == Act.None) mode = Mode.Idle;
            if (mode == Mode.Sleeping) Wake(quiet: true);
        }
        Store.Log($"Show: {s.Id} asked for in chat");
        if (CueReady(s)) return Perform(s) is { } why ? "✗ " + why : $"✓ {s.Name}";
        cued = s; cuedUntil = now + Math.Max(10, Store.Config.ShowWaitSeconds);
        return s.Cue == Cue.AfterChat && chat != null ? $"✓ {s.Name}, once the chat's closed" : $"✓ {s.Name}, in a moment";
    }

    /// <summary>
    /// Free to do it: on his feet with nothing else going on. Ones that wait for the chat also want no bubble, call,
    /// full screen or locked PC, since his daemon would vanish at once for any of those.
    /// </summary>
    bool CueReady(Skill s) =>
        mode == Mode.Idle && !peeking && !hidden && !autoHidden && currentNudge == null && pendingAct == Act.None && !AtLunch && !Coding
        && (s.Cue == Cue.Now || (chat == null && !thinking && Bubble.Visibility != Visibility.Visible
                                 && !Services.Hub.Quiet && !Services.Hub.Presence.Locked));

    /// <summary>Clears whatever little activity he was in the middle of, then starts the skill. Null if it started, else why not.</summary>
    string? Perform(Skill s)
    {
        act = Act.None; sitSub = Act.None;
        var why = s.Perform?.Invoke(this);
        if (why == null) Hurry();
        return why;
    }

    /// <summary>Every frame (from UpdateActs): ends a nap that was only for show, and starts a cued skill once it can be seen.</summary>
    void UpdateShow()
    {
        // Woken some other way (a click, the chat) and the show nap is simply over.
        if (showNapUntil > 0 && (mode != Mode.Sleeping || now >= showNapUntil))
        {
            if (mode == Mode.Sleeping) Wake();
            showNapUntil = -1;
        }
        if (cued == null) return;
        if (now > cuedUntil) { Store.Log($"Show: {cued.Id} dropped, no chance to do it"); cued = null; return; }
        if (!CueReady(cued)) return;
        var s = cued; cued = null;
        if (Perform(s) is { } why) Say(why, 4);   // the chat's gone by now, so a bubble says why not
    }

    string? ShowIdle(Act a)
    {
        StartIdleAct(a, ScreenAt(x));
        return null;
    }

    string? ShowClimb()
    {
        if (ClimbRoute(ScreenAt(x)) is not { } route) return "No window edge I can climb from here.";
        StartIdleAct(Act.Climb, ScreenAt(x), route);
        return null;
    }

    string? ShowDaemon(string? routine, bool? shift)
    {
        if (daemonOut) return "It's already out.";
        if (Perched) return "Not up here. It needs the floor.";
        int side = SideWithRoom(60);
        StartDaemon(side == 0 ? -facing : side, routine, shift);
        return null;
    }

    string? ShowNap()
    {
        Sleep(requested: true);   // requested, so moving the mouse doesn't wake him straight away; UpdateShow does
        showNapUntil = now + Math.Max(3, Store.Config.ShowSeconds);
        return null;
    }

    string? ShowLaptop()
    {
        codeDebugUntil = now + Math.Max(3, Store.Config.ShowSeconds);   // a pretend job: UpdateCoding takes him to the laptop, then he nods
        return null;
    }

    string? ShowKnock()
    {
        Knock();
        return null;
    }

    string? ShowPopper()
    {
        Pop();
        return null;
    }
}
