using System;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// Lunch break. When you head off for lunch (the lunch nudge's "Going", telling him in chat, or right-click → Lunch
/// break) a LunchMinutes timer starts and Vex has his own: walks to the nearer corner, puts his mug down beside him,
/// sits, opens his lunch box and eats a sandwich for a random LunchEatMinMinutes–LunchEatMaxMinutes, then shuts the
/// box and naps for the rest. Time's up: he wakes with a flinch and a dry line. You back early (input after being
/// away, "I'm back" in chat, clicking him while he naps, or End lunch): he wakes groggily instead.
/// The routine survives being dragged, hopped or interrupted by a nudge: he sits down again wherever he ends up and
/// carries on (same timer). Chat, the menu and important nudges still work; casual bubbles wait till he's awake.
/// It takes priority over the coding laptop, which picks up again afterwards if the job's still going.
/// Setup → Behaviour turns it off (LunchRoutine). The break clock beside him counts the same timer down (VexBreakClock.cs).
/// </summary>
public partial class VexWindow
{
    double lunchUntil = -1;     // when his lunch break ends; -1 when he's not on one
    double lunchEatFor;         // how long he'll eat for this time (seconds), picked when lunch starts
    double eatEnd = -1;         // when the sandwich goes away and the nap starts; -1 until he first sits down
    double eatStart;
    double lunchOverAt = -99;   // when the last one ended, so "welcome back" doesn't talk over his wake-up line
    bool lunchSawAway;          // you've been away since lunch started, so the next keyboard/mouse input means you're back
    int lunchWake;              // 0 still on lunch; else how it's ending: 1 time's up (flinch), 2 back early from his nap (groggy), 3 packing up mid-meal
    double lunchWakeAt;
    bool lunchRoused; double rousedAt = -99;   // eyes open for an important nudge mid-nap

    /// <summary>On his lunch break: walking to his spot, eating, napping or just waking up.</summary>
    bool AtLunch => lunchUntil > 0;

    /// <summary>Napping through the second half of his lunch: casual bubbles and wellbeing nudges are held back.</summary>
    public bool LunchNapping => act == Act.Lunch && lunchWake == 0 && eatEnd > 0 && now >= eatEnd;

    /// <summary>At lunch, or only just woken from it (his own wake-up line already covers "welcome back").</summary>
    public bool LunchRecent => AtLunch || now - lunchOverAt < 90;

    /// <summary>A line for the chat's context while he's at lunch, so "I'm back" makes sense to the brain. Null otherwise.</summary>
    public string? LunchStatus()
    {
        if (!AtLunch || lunchWake != 0) return null;
        var ends = DateTime.Now.AddSeconds(lunchUntil - now);
        return $"You're on your own lunch break (it ends {ends:HH:mm}), {(LunchNapping ? "napping" : "having a sandwich")}.";
    }

    /// <summary>
    /// You're off for lunch: start the break clock, the timer, and head to the corner. With LunchRoutine off only the
    /// clock runs (VexBreakClock.cs); already at lunch, nothing changes.
    /// </summary>
    public void StartLunch()
    {
        var c = Store.Config;
        if (AtLunch) return;
        double total = Math.Max(2, c.LunchMinutes);
        StartBreakClock(BreakKind.Lunch, total * 60);
        if (!c.LunchRoutine) return;
        if (mode == Mode.Sleeping) Wake(quiet: true);

        double lo = Math.Max(0.5, Math.Min(c.LunchEatMinMinutes, c.LunchEatMaxMinutes)), hi = Math.Max(lo, c.LunchEatMaxMinutes);
        lunchEatFor = Math.Min(lo + rng.NextDouble() * (hi - lo), total / 2) * 60;   // always leaves time for a nap
        lunchUntil = now + total * 60;
        eatEnd = -1; lunchWake = 0; lunchSawAway = false;

        act = Act.None; peeking = false;   // lunch beats the laptop: a running build carries on without him
        if (mode == Mode.Climbing) { LetGo(slipped: false); return; }   // down off the wall first; UpdateLunch sits him where he lands
        var scr = ScreenAt(x);
        if (Perched) { BeginLunch(scr); return; }      // lunch on the window top, then

        var wa = scr.WorkingArea;
        double half = BodyHalfW * S;
        double left = wa.Left + half + 2 * S, right = wa.Right - half - 2 * S;
        double tx = x - left < right - x ? left : right;
        if (Math.Abs(tx - x) < 3 * S) { BeginLunch(scr); return; }
        pendingAct = Act.Lunch; pendingX = tx;
        WalkTo(tx, Store.Config.WalkSpeed * 1.5);       // a man on a mission
    }

    /// <summary>Sit down to (or back to) lunch where he is: back to the screen edge if he made it to the corner.</summary>
    void BeginLunch(Forms.Screen scr)
    {
        var wa = scr.WorkingArea;
        double half = BodyHalfW * S;
        bool atLeft = Math.Abs(x - (wa.Left + half + 2 * S)) < 3 * S, atRight = Math.Abs(x - (wa.Right - half - 2 * S)) < 3 * S;
        if (!Perched && (atLeft || atRight)) { facing = atLeft ? 1 : -1; sitLean = atLeft ? -5 : 5; }
        else sitLean = 0;
        Begin(Act.Lunch, double.PositiveInfinity);     // UpdateLunch decides when it's over
        if (eatEnd < 0) { eatStart = now + 2; eatEnd = Math.Min(eatStart + lunchEatFor, lunchUntil); }
    }

    /// <summary>
    /// Called every frame from UpdateActs: watches the timer and for you coming back, and sits him down again after
    /// he's been moved (dragged, hopped, or stopped by a nudge on the way to his corner).
    /// </summary>
    void UpdateLunch(Forms.Screen scr)
    {
        if (!AtLunch || lunchWake != 0) return;
        if (now >= lunchUntil) { WakeFromLunch(timeUp: true); return; }

        // Back early? Only once you've actually been gone (locked, or idle LunchAwayMinutes), so the clicks right after
        // "Going" don't count; eat at the desk and he just naps through to the end.
        var idle = Native.IdleTime();
        if (Services.Hub.Presence.Locked || idle.TotalMinutes >= Store.Config.LunchAwayMinutes) lunchSawAway = true;
        else if (lunchSawAway && idle.TotalSeconds < 2) { WakeFromLunch(timeUp: false); return; }

        if (act == Act.Lunch && peeking) actEnd = Math.Min(actEnd, now + 0.35);   // nowhere clear to stand: no room to sit
        if (act == Act.Lunch || pendingAct == Act.Lunch) return;
        if (mode != Mode.Idle || peeking || hidden || autoHidden) return;
        if (act != Act.None) { actEnd = Math.Min(actEnd, now + 0.6); return; }
        BeginLunch(scr);
    }

    /// <summary>Lunch is over, one way or another: play the wake-up (if he's sitting) and say so.</summary>
    void WakeFromLunch(bool timeUp)
    {
        if (!AtLunch || lunchWake != 0) return;
        bool napping = LunchNapping;
        StopBreakClock(cue: timeUp);   // the countdown ends with his lunch; his wake-up line below is the bubble
        lunchWake = !napping ? 3 : timeUp ? 1 : 2;
        lunchWakeAt = now;
        if (act == Act.Lunch) actEnd = now + (lunchWake == 1 ? 2.0 : lunchWake == 2 ? 2.8 : 1.4);
        else
        {
            // Not sat down (walking there, being carried, ducked): nothing to pack away.
            if (pendingAct == Act.Lunch) { pendingAct = Act.None; if (mode == Mode.Walking) mode = Mode.Idle; }
            FinishLunch();
        }
        Say(lunchWake switch
        {
            1 => Pick("Lunch is over. I've been informed.", "That's lunch. Back to it, apparently.", "Lunch over. I was resting my eyes. Professionally."),
            2 => Pick("Mm. You're back. Early. I was… guarding.", "Hm? Oh. Already? Fine.", "Wasn't asleep. Lunch is over, then."),
            _ => timeUp ? "Lunch over. I didn't even finish my sandwich." : Pick("Back already? I was mid-sandwich.", "Oh. Right. Lunch over, then."),
        }, 5);
    }

    void FinishLunch()
    {
        lunchUntil = -1; eatEnd = -1; lunchWake = 0; lunchSawAway = false; lunchRoused = false;
        lunchOverAt = now;
        Zzz.Visibility = Visibility.Collapsed;
        nextWander = now + 8;
    }

    /// <summary>You're back early: said so in chat ([[break: back]]), End lunch on the menu, or clicked him mid-nap.</summary>
    public void LunchBack()
    {
        if (!AtLunch && LunchClockOn) StopBreakClock(cue: false);   // no routine running: just the clock to put away
        WakeFromLunch(timeUp: false);
    }

    void LunchPose(ref double eyeCap, ref double? turnOverride, ref double? lookY)
    {
        double k = Blend(actStart, actEnd, 0.8);
        Breath.Y += 9 * k;                                  // sat down, like Sit
        LeftFootT.X -= 4 * k; RightFootT.X += 4 * k;
        Lean.Angle = sitLean * k;
        LunchBox.Opacity = k;
        RightGloveT.X += -8 * k; RightGloveT.Y += 16 * k;   // mug's on the floor; that mitten rests in his lap
        Zzz.Visibility = Visibility.Collapsed;

        double t = now - actStart;
        if (lunchWake != 0) WakePose(ref eyeCap, ref turnOverride, ref lookY);
        else if (now < eatEnd)
        {
            // Lid opens, sandwich comes out, then bite / chew cycles every 6 s. The sandwich gets smaller as time goes.
            LidR.Angle = -80 * Smooth(t / 1.2);
            double sk = Smooth(Math.Min((t - 1.4) / 0.6, (eatEnd - now) / 0.6));
            Sandwich.Opacity = sk > 0.02 ? 1 : 0;
            double eaten = Math.Clamp((now - eatStart) / Math.Max(1, eatEnd - eatStart), 0, 1);
            SandwichS.ScaleX = SandwichS.ScaleY = 1 - 0.85 * eaten;

            double ct = t < 2 ? 99 : (t - 2) % 6.0;
            double raise = ct < 0.7 ? Smooth(ct / 0.7) : ct < 1.6 ? 1 : ct < 2.3 ? 1 - Smooth((ct - 1.6) / 0.7) : 0;
            LeftGloveT.X += (14 + 15 * raise) * sk;
            LeftGloveT.Y += (-6 - 26 * raise) * sk;
            if (ct >= 1.0 && ct < 4) Breath.Y += Math.Sin(now * 14) * 0.5;      // chewing
            eyeCap = Math.Min(eyeCap, raise > 0.5 ? 0.35 : 0.85);
            turnOverride = -5; lookY = 2.5;
        }
        else
        {
            // Box shut, eyes shut, head nodding. Zzz till the end of lunch. Something important (a knock and its
            // bubble) opens his eyes while it's up; then he drifts off again.
            LidR.Angle = -80 * Math.Max(0, 1 - (now - eatEnd) / 0.8);
            bool roused = Knocking || (currentNudge != null && Bubble.Visibility == Visibility.Visible);
            if (roused != lunchRoused) { lunchRoused = roused; rousedAt = now; }
            double r = Smooth((now - rousedAt) / 0.5);
            double doze = Smooth((now - Math.Max(eatEnd, actStart + 0.8)) / 1.5) * (roused ? 1 - r : r);
            eyeCap = Math.Min(eyeCap, 1 - 0.88 * doze);
            Breath.Y += 1.5 * doze + Math.Sin(now * 1.2) * 0.8 * doze;            // slumped, slow breathing
            HoodT.Y += 1.5 * doze;
            Zzz.Visibility = doze > 0.6 && k > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            turnOverride = 0; lookY = 2 * doze;
        }

        // Mug on the floor by his side (as at the laptop): lowered to the ground whatever his breathing does.
        MugT.X += 13 * k;
        MugT.Y += k * (42 + Math.Abs(sitLean) * 0.7 * k - Breath.Y - MugT.Y);
    }

    /// <summary>The last couple of seconds of lunch: a jolt awake, a groggy surfacing, or the lid snapping shut, then up.</summary>
    void WakePose(ref double eyeCap, ref double? turnOverride, ref double? lookY)
    {
        double w = now - lunchWakeAt;
        switch (lunchWake)
        {
            case 1:
                // Small flinch: a jolt up, mittens twitch, eyes snap open, a quick look round.
                double jolt = w < 0.12 ? Smooth(w / 0.12) : Math.Max(0, 1 - (w - 0.12) / 0.35);
                Breath.Y -= 4 * jolt;
                Squash.ScaleY *= 1 + 0.07 * jolt; Squash.ScaleX *= 1 - 0.04 * jolt;
                LeftGloveT.X -= 3 * jolt; LeftGloveT.Y -= 6 * jolt;
                RightGloveT.X += 3 * jolt; RightGloveT.Y -= 6 * jolt;
                turnOverride = Math.Sin(w * 6) * 18 * Math.Max(0, 1 - w / 1.4);
                lookY = 0;
                break;
            case 2:
                // Groggy: eyes come open slowly and only halfway, head lolls, still slumped for a moment.
                double up = Smooth(w / 1.8);
                eyeCap = Math.Min(eyeCap, 0.12 + 0.48 * up);
                Breath.Y += 1.5 * (1 - up);
                HoodT.Y += 1.5 * (1 - up);
                turnOverride = Math.Sin(w * 2.2) * 10 * (1 - 0.5 * up);
                lookY = 2 * (1 - up);
                break;
            default:
                // Still eating: the lid snaps shut, the sandwich goes in, up he gets.
                LidR.Angle = -80 * Math.Max(0, 1 - w / 0.4);
                break;
        }
    }
}
