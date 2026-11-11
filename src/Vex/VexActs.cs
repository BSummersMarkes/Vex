using System;
using System.Collections.Generic;
using System.Linq;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>Little things Vex does while he's just standing about.</summary>
enum Act { None, Sip, Phone, Read, Sit, Stretch, Watch, Lunch, Code, Juggle, Climb }

/// <summary>One of the things he may do when he's bored: its config id, its name in Setup, and whether it's on by default.</summary>
record IdleAct(Act Act, string Id, string Name, bool DefaultOn = true);

/// <summary>
/// The one list of idle activities. The picker in UpdateActs and Setup → Behaviour → Idle activities both read it, so
/// a new one only needs adding here (and a case in UpdateActs/Pose). Lunch and the coding laptop aren't idle: they're
/// triggered by what you're doing, and have their own switches.
/// </summary>
static class IdleActs
{
    public static readonly IdleAct[] All =
    {
        new(Act.Sip, "sip", "Sip coffee"),
        new(Act.Phone, "phone", "Check his phone"),
        new(Act.Read, "read", "Read a book"),
        new(Act.Sit, "sit", "Sit against the screen edge"),
        new(Act.Stretch, "stretch", "Stretch"),
        new(Act.Watch, "watch", "Check his watch (meeting soon)"),
        new(Act.Juggle, "juggle", "Juggle"),
        new(Act.Climb, "climb", "Climb windows"),
    };

    /// <summary>Whether he may do this when bored (config.json IdleActivities, else its default). Anything not in the list is always allowed.</summary>
    public static bool Enabled(Act a)
    {
        var e = All.FirstOrDefault(i => i.Act == a);
        if (e == null) return true;
        return Store.Config.IdleActivities.TryGetValue(e.Id, out var on) ? on : e.DefaultOn;
    }
}

/// <summary>
/// Idle activities. One at a time: ease in, hold, ease out. Anything that needs him to move
/// (walking, dodging, being dragged) cuts the current one short. Some are nudged by context:
/// a meeting soon → he checks his watch; you've been sat a while → he stretches (a hint).
/// </summary>
public partial class VexWindow
{
    Act act = Act.None, pendingAct = Act.None, sitSub = Act.None;
    double actStart, actEnd, nextAct = 15, lastSip = -999, lastWatch = -999, sitLean, pendingX, nextFlip, flipStart = -1;

    double MugLevel() => Math.Clamp(1 - (DateTime.Now - Store.State.RefilledAt).TotalMinutes / Store.Config.MugDrainMinutes, 0, 1);

    /// <summary>Called every frame. Ends finished activities, cuts them short when he has to move, starts new ones.</summary>
    void UpdateActs(Forms.Screen scr)
    {
        if (act != Act.None && mode != Mode.Idle && actEnd > now + 0.35) actEnd = now + 0.35;
        if (act != Act.None && now >= actEnd)
        {
            if (act == Act.Sip) squashUntil = now + 0.18; // "ahh"
            if (act == Act.Lunch)
            {
                Zzz.Visibility = System.Windows.Visibility.Collapsed;
                if (lunchWake != 0) FinishLunch();   // stood up after waking; otherwise he was moved and sits down again
            }
            act = Act.None; sitSub = Act.None;
            nextAct = now + 12 + rng.NextDouble() * 25;
        }
        if (mode is Mode.Dragging or Mode.Hopping or Mode.Sleeping or Mode.Falling) pendingAct = Act.None;
        UpdateLunch(scr);    // lunch timer, you coming back, sitting down again after being moved (VexLunch.cs)
        UpdateCoding(scr);   // off to the laptop while code is being written (VexCoding.cs)
        if (mode != Mode.Idle || act != Act.None || peeking || thinking || hovering || hidden || autoHidden || currentNudge != null) return;

        // Arrived at the screen edge to sit down (or have lunch, or code), or at the foot or top of a window edge to climb?
        if (pendingAct is Act.Sit or Act.Lunch or Act.Code or Act.Climb)
        {
            var was = pendingAct;
            pendingAct = Act.None;
            if (Math.Abs(x - pendingX) < 3 * S)
            {
                if (was == Act.Lunch) BeginLunch(scr); else if (was == Act.Code) BeginCode(scr, againstEdge: true);
                else if (was == Act.Climb) BeginClimb(); else StartSit(scr);
                return;
            }
        }
        if (now < nextAct || daemonOut) return;   // with his daemon out he just watches it
        if (TryDaemon()) return;                  // due out, and all's quiet: that's this time's idle action (VexDaemon.cs)

        var options = new List<(Act Act, double Weight)>
        {
            (Act.Phone, 1.4), (Act.Read, 1.2), (Act.Sit, 1.0),
            (Act.Stretch, Services.Hub.Presence.Sitting.TotalMinutes > 45 ? 2.0 : 0.4),
        };
        if (MugLevel() > 0.02 && now - lastSip > 50) options.Add((Act.Sip, 4));
        bool meetingSoon = Services.Hub.Calendar.Upcoming.Any(m => (m.Start - DateTime.Now).TotalMinutes is > 0 and < 15);
        if (meetingSoon && now - lastWatch > 300) options.Add((Act.Watch, 6));
        options.Add((Act.Juggle, 0.6));   // a rare bit of showing off
        // Climbing needs a clear edge to go up or down, so the route is found first and he only picks it if there is one.
        var route = ClimbAllowed() ? ClimbRoute(scr) : null;
        if (route != null) options.Add((Act.Climb, 0.8));
        // Only what's switched on in Setup; with nothing left he just stands there and tries again later.
        options.RemoveAll(o => !IdleActs.Enabled(o.Act));
        if (options.Count == 0) { nextAct = now + 12 + rng.NextDouble() * 25; return; }

        double roll = rng.NextDouble() * options.Sum(o => o.Weight);
        var pick = options.First().Act;
        foreach (var o in options) { if ((roll -= o.Weight) <= 0) { pick = o.Act; break; } }

        switch (pick)
        {
            case Act.Sip: Begin(Act.Sip, 2.8); lastSip = now; break;
            case Act.Phone: Begin(Act.Phone, 7 + rng.NextDouble() * 7); break;
            case Act.Read: Begin(Act.Read, 12 + rng.NextDouble() * 14); nextFlip = now + 3; break;
            case Act.Stretch: Begin(Act.Stretch, 2.6); break;
            case Act.Watch: Begin(Act.Watch, 2.4); lastWatch = now; break;
            case Act.Sit: GoSit(scr); break;
            case Act.Juggle: StartJuggle(); break;
            case Act.Climb: GoClimb(route!); break;
        }
    }

    /// <summary>Testing aid (debug endpoint): start an activity now.</summary>
    internal string DebugAct(string name)
    {
        // His daemon isn't an Act (he carries on being himself while it plays): "daemon" or "daemon <routine>".
        if (name.Trim().StartsWith("daemon", StringComparison.OrdinalIgnoreCase)) return DebugDaemon(name.Trim().Substring(6).Trim());
        if (!Enum.TryParse<Act>(name.Trim(), true, out var a) || a == Act.None) return "unknown act";
        act = Act.None; mode = Mode.Idle;
        switch (a)
        {
            case Act.Sit: GoSit(ScreenAt(x)); break;
            case Act.Lunch: StartLunch(); break;
            case Act.Code: codeDebugUntil = now + 15; break;   // pretends a job runs for 15 s, then nods
            case Act.Read: Begin(a, 20); nextFlip = now + 2; break;
            case Act.Juggle: StartJuggle(); break;
            case Act.Climb:
                if (ClimbRoute(ScreenAt(x)) is not { } route) return "no climbable edge from here";
                GoClimb(route); break;
            default: Begin(a, a is Act.Sip or Act.Stretch or Act.Watch ? 3 : 15); break;
        }
        return "ok";
    }

    void Begin(Act a, double seconds) { act = a; actStart = now; actEnd = now + seconds; }

    /// <summary>Wander to the nearer edge of the screen, then sit leaning against it.</summary>
    void GoSit(Forms.Screen scr)
    {
        if (Perched) { ReadInstead(); return; } // no screen edge up here
        var wa = scr.WorkingArea;
        double half = BodyHalfW * S;
        double left = wa.Left + half + 2 * S, right = wa.Right - half - 2 * S;
        double tx = x - left < right - x ? left : right;
        if (ForegroundRect() is { } r && Overlaps(r, tx, wa.Bottom)) { ReadInstead(); return; }
        if (Math.Abs(tx - x) < 3 * S) { StartSit(scr); return; }
        pendingAct = Act.Sit; pendingX = tx;
        WalkTo(tx, Store.Config.WalkSpeed);
    }

    /// <summary>Can't sit at the edge: read where he stands instead, or, with reading switched off, leave it for a while.</summary>
    void ReadInstead()
    {
        if (IdleActs.Enabled(Act.Read)) { Begin(Act.Read, 15); nextFlip = now + 3; }
        else nextAct = now + 12 + rng.NextDouble() * 25;
    }

    void StartSit(Forms.Screen scr)
    {
        bool edgeLeft = x - scr.WorkingArea.Left < scr.WorkingArea.Right - x;
        facing = edgeLeft ? 1 : -1;          // back to the edge, looking into the screen
        sitLean = edgeLeft ? -5 : 5;         // top of the hood rests against the edge
        double r = rng.NextDouble();
        sitSub = r < 0.5 ? Act.Read : r < 0.8 ? Act.Phone : Act.None;
        if (!IdleActs.Enabled(sitSub)) sitSub = Act.None;   // book or phone switched off: he just sits and looks about
        Begin(Act.Sit, 35 + rng.NextDouble() * 45);
        nextFlip = now + 5;
    }

    static double Smooth(double k) { k = Math.Clamp(k, 0, 1); return k * k * (3 - 2 * k); }

    double Blend(double start, double end, double ramp) =>
        Smooth(Math.Min((now - start) / ramp, (end - now) / ramp));

    /// <summary>Applies the current activity on top of the normal pose. Overrides for the eyes come back via refs.</summary>
    void ActPose(ref double eyeCap, ref double? turnOverride, ref double? lookY)
    {
        // Reset the parts only activities move.
        MugT.X = 0; RightGloveT.X = 0; MugR.Angle = 0; Lean.Angle = 0; HoodT.Y = 0;
        Book.Opacity = 0; Phone.Opacity = 0; Sandwich.Opacity = 0; LunchBox.Opacity = 0; Laptop.Opacity = 0; Balls.Opacity = 0;
        if (act == Act.None) return;
        if (act == Act.Lunch) { LunchPose(ref eyeCap, ref turnOverride, ref lookY); return; }
        if (act == Act.Code) { CodePose(ref eyeCap, ref turnOverride, ref lookY); return; }
        if (act == Act.Juggle) { JugglePose(ref eyeCap, ref turnOverride, ref lookY); return; }

        if (act == Act.Sit)
        {
            double k = Blend(actStart, actEnd, 0.8);
            Breath.Y += 9 * k;                       // settle down onto the hem
            LeftFootT.X -= 4 * k; RightFootT.X += 4 * k;
            Lean.Angle = sitLean * k;
            if (sitSub != Act.None)
                Pose(sitSub, Blend(actStart + 1.5, actEnd - 1.2, 0.6), ref eyeCap, ref turnOverride, ref lookY);
            else if (k > 0.5) { turnOverride = Math.Sin(now * 0.35) * 25; } // just looking about
            return;
        }
        Pose(act, Blend(actStart, actEnd, act is Act.Sip or Act.Stretch or Act.Watch ? 0.6 : 0.5), ref eyeCap, ref turnOverride, ref lookY);
    }

    void Pose(Act a, double k, ref double eyeCap, ref double? turnOverride, ref double? lookY)
    {
        if (k <= 0) return;
        switch (a)
        {
            case Act.Sip:
                // Mug up to where a mouth would be, tip it, eyes shut. Bliss.
                MugT.X += -19 * k; MugT.Y += -30 * k;
                RightGloveT.X += -19 * k; RightGloveT.Y += -30 * k;
                MugR.Angle = -35 * k - (k >= 1 ? 6 * Math.Sin((now - actStart) * 3) : 0);
                eyeCap = Math.Min(eyeCap, 1 - 0.85 * k);
                turnOverride = 0; lookY = 0;
                break;

            case Act.Phone:
                LeftGloveT.X += 21 * k; LeftGloveT.Y += -5 * k + (k >= 1 && Math.Sin(now * 1.3) > 0.97 ? -0.8 : 0); // the odd scroll
                Phone.Opacity = Math.Min(1, k * 2);
                PhoneScreen.Opacity = 0.75 + 0.2 * Math.Sin(now * 2.1);
                turnOverride = -12 * k; lookY = 3.2 * k;
                break;

            case Act.Read:
                LeftGloveT.X += 16 * k; LeftGloveT.Y += -12 * k;
                Book.Opacity = Math.Min(1, k * 2);
                turnOverride = -7 * k; lookY = 3.5 * k;
                if (k >= 1 && flipStart < 0 && now >= nextFlip) flipStart = now;
                if (flipStart >= 0)
                {
                    // Turn a page: the right-hand page swings over to the left.
                    double f = (now - flipStart) / 0.55;
                    if (f >= 1) { flipStart = -1; nextFlip = now + 4 + rng.NextDouble() * 5; FlipPage.Opacity = 0; }
                    else { FlipPage.Opacity = 1; FlipT.ScaleX = Math.Cos(f * Math.PI); }
                }
                break;

            case Act.Stretch:
                LeftGloveT.X += -6 * k; LeftGloveT.Y += -40 * k;
                RightGloveT.X += 4 * k; RightGloveT.Y += -34 * k; MugT.X += 4 * k; MugT.Y += -34 * k;
                Squash.ScaleY *= 1 + 0.06 * k; Squash.ScaleX *= 1 - 0.03 * k;
                eyeCap = Math.Min(eyeCap, 1 - 0.65 * k); lookY = -3 * k;
                break;

            case Act.Watch:
                LeftGloveT.X += 16 * k; LeftGloveT.Y += -22 * k;
                turnOverride = -15 * k; lookY = 3 * k;
                break;
        }
    }
}
