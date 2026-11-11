using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Vex;

/// <summary>Shapes his daemon can take. The cat is its settled form: it always comes out, and goes home, as a cat.</summary>
enum DaemonForm { Cat, Mouse, Moth, Bird }

/// <summary>What the daemon's body is doing. Each form draws it its own way (a moth "sitting" has landed, say).</summary>
enum DaemonPose { Stand, Run, Sit, Crouch, Curl, Sleep, Stretch, Bat }

/// <summary>How one step of a routine moves it: stay put, walk along the floor, hop in an arc, or ride on Vex.</summary>
enum DaemonMove { Stay, Walk, Hop, Pin }

/// <summary>
/// One step of a daemon routine. Targets are worked out afresh every frame (they're functions), so anything aimed at
/// Vex (his shoulder, his pocket, the spot beside him) follows him as he breathes, turns or walks off.
/// </summary>
sealed class DaemonStep
{
    public DaemonMove Move;
    public DaemonPose Pose;
    /// <summary>How long it lasts; for a walk, the most it may take (it normally ends on arrival).</summary>
    public double Seconds;
    public Func<(double X, double Y)>? To;
    public double Speed, Height;   // walking speed in DIP/s; a hop's height in DIPs
    public DaemonForm? Shift;      // turn into this as the step starts
    public Action? Start;          // anything else to do as it starts (Vex flinching at a pounce)
    public int Face;               // 0: whichever way it's going; 1: towards Vex; -1: away from him; 2: towards the mote
    public bool In, Out;           // fade in (popping out) or out (going home) over the step
    public bool Follow;            // a walk that isn't over while Vex is still walking (keeping up with him)
    public bool OnHim;             // riding on Vex (his shoulder or pocket), not on the floor

    public DaemonStep Shifting(DaemonForm f) { Shift = f; return this; }
    public DaemonStep Facing(int f) { Face = f; return this; }
    public DaemonStep FadeIn() { In = true; return this; }
    public DaemonStep FadeOut() { Out = true; return this; }
    public DaemonStep Then(Action a) { Start = a; return this; }
}

/// <summary>
/// His daemon: a small animal friend, after the daemons in His Dark Materials, who lives in his hoodie and now and
/// then comes out to play when he's bored. Usually a cat; sometimes it shapeshifts mid-play (cat, mouse, moth, bird
/// and back to cat), with a quick shrink, a puff and a grow.
///
/// When: one of his idle-time picks (UpdateActs), only when it's due (DaemonFrequency: on average once every
/// DaemonRare/Sometimes/OftenMinutes of him running, never sooner than DaemonCooldownMinutes after the last time), and
/// only while he's standing about on the taskbar with nothing else on, you're at the desk (input in the last couple of
/// minutes), and there's no call, full-screen app, chat, bubble or nudge. Setup → Behaviour turns it off.
///
/// What: it pops out of his hoodie pocket (or is just there beside him), looks up at him, then does one of: a run
/// along the taskbar and back; a sit on his shoulder; chasing a little glowing mote; curling up beside him for a
/// moment; or stalking and pouncing on his boots (he flinches). Then it hops back into the pocket or fades away.
/// If he dozes off or goes to lunch while it's out, it curls up asleep beside him until he's up again.
///
/// Out of the way: it retreats at once (a quick fade) for a bubble, a nudge, the chat opening, him thinking, being
/// picked up, hopping, climbing or going to the laptop, a call or full screen, the PC locking or the setting going
/// off. If he walks off mid-game it trots back to him and goes in. It stays on his monitor, never runs past the
/// window you're in, and is click-through while it moves; sitting still it can be clicked (a hop and a wiggle, or a
/// sleepy look if it's asleep), and that's all a click does. No sound.
///
/// Drawn in its own small window (DaemonWindow), driven from his frame loop and clock, so it's paced with him (VexPace)
/// and costs next to nothing: a few flat shapes, no blur effects.
/// </summary>
public partial class VexWindow
{
    /// <summary>His boot soles in the rig's units, for working out where his pocket and shoulder are.</summary>
    const double RigSole = 148.5;

    DaemonWindow? daemon;
    bool daemonOut, daemonGoing, dmAsleep, dmHeadingBack, dmMoving, dmMote;
    double dmX, dmY, dmFromX, dmFromY;   // its feet, physical pixels like his; where the current step started
    double dmHop, dmSize = 1, dmAlpha, dmStepAt, dmGoStart, dmGoAlpha, dmPokeAt = -99, dmPounced = -99, dmRaisedFor;
    int dmFacing = 1, dmSide = 1, dmIx = -1;
    double dmSideAt = -99;
    DaemonForm dmForm = DaemonForm.Cat;
    DaemonPose dmPose = DaemonPose.Sit;
    List<DaemonStep> dmSteps = new();
    double nextDaemon = -1;               // when it's next due out (seconds since start); -1 = not worked out yet
    string dmScheduledFor = "";            // the frequency that was worked out with, so a change in Setup takes effect
    double moteX, moteH, moteToX, moteToH, moteCx, nextMoteMove, moteCaught = -1;

    /// <summary>Riding on him (sitting on his shoulder, or peeking out of his pocket) rather than on the floor.</summary>
    bool DaemonOnHim => dmIx >= 0 && dmIx < dmSteps.Count && dmSteps[dmIx].OnHim;

    /// <summary>Full frame rate while it's out and moving (VexPace.cs); curled up asleep it only breathes.</summary>
    bool DaemonLively => daemonOut && (daemonGoing || dmMoving || dmMote || now - dmPokeAt < 1 || daemon?.Shifting == true
                                       || dmPose is not (DaemonPose.Curl or DaemonPose.Sleep));

    /// <summary>
    /// When it's next due: the cooldown, plus a random wait averaging the rest of the chosen frequency's gap. The wait
    /// is exponential, so some visits come soon after the cooldown and some much later, and it never feels timed.
    /// </summary>
    void ScheduleDaemon()
    {
        var c = Store.Config;
        double mean = c.DaemonFrequency switch { "rare" => c.DaemonRareMinutes, "often" => c.DaemonOftenMinutes, _ => c.DaemonSometimesMinutes };
        double gap = Math.Max(1, c.DaemonCooldownMinutes), extra = Math.Max(1, mean - gap);
        nextDaemon = now + 60 * (gap - Math.Log(1 - rng.NextDouble()) * extra);
        dmScheduledFor = c.DaemonFrequency;
    }

    /// <summary>
    /// Called by the idle picker (UpdateActs) when he's bored, before anything else is chosen: if the daemon's due
    /// and everything's quiet, out it comes instead. UpdateActs only gets here with him idle on his feet, no chat,
    /// nudge, hover, hiding or activity, so this checks the rest.
    /// </summary>
    bool TryDaemon()
    {
        var c = Store.Config;
        if (!c.Daemon) return false;
        if (nextDaemon < 0 || dmScheduledFor != c.DaemonFrequency) ScheduleDaemon();
        if (now < nextDaemon) return false;
        if (Perched || chat != null || Bubble.Visibility == Visibility.Visible || AtLunch || Coding
            || Services.Hub.Quiet || Services.Hub.Presence.Away) return false;   // Away: no input for a couple of minutes, or locked
        int side = SideWithRoom(60);
        if (side == 0) return false;
        StartDaemon(side, null);
        return true;
    }

    /// <summary>Testing aid (debug endpoint, "daemon" or e.g. "daemon shoulder"): bring it out now, optionally for one routine.</summary>
    internal string DebugDaemon(string routine)
    {
        if (daemonOut) return "already out";
        int side = SideWithRoom(60);
        StartDaemon(side == 0 ? -facing : side, routine.Length > 0 ? routine.ToLowerInvariant() : null);
        return "ok";
    }

    /// <summary>Out it comes, with a routine (picked at random unless one's asked for) and the way home queued after it.</summary>
    void StartDaemon(int side, string? want)
    {
        daemon ??= new DaemonWindow(PokeDaemon);
        dmSide = side; dmForm = DaemonForm.Cat;
        dmAsleep = dmHeadingBack = daemonGoing = dmMote = false;
        dmPokeAt = dmPounced = -99; dmHop = 0; dmAlpha = 0; dmSize = 0.35;
        daemonOut = true;
        bool shift = rng.NextDouble() < Store.Config.DaemonShiftChance;
        var steps = new List<DaemonStep>();

        // Out of his hoodie pocket, or just there beside him as if it had been all along.
        if (rng.NextDouble() < 0.6)
        {
            (dmX, dmY) = Pocket();
            steps.Add(Pin(Pocket, DaemonPose.Sit, 0.45).FadeIn().Facing(-1));
            steps.Add(Hop(() => Beside(52), 0.5, 20));
        }
        else
        {
            (dmX, dmY) = Beside(52);
            steps.Add(Stay(DaemonPose.Sit, 0.5).FadeIn().Facing(1));
        }
        steps.Add(Stay(DaemonPose.Sit, 0.7 + rng.NextDouble() * 0.6).Facing(1));   // a look up at him first

        double? far = RunSpot();
        var routines = new List<string> { "shoulder", "play", "curl", "pounce" };
        if (far != null) routines.Add("run");
        string routine = want != null && (routines.Contains(want) || want == "run") ? want : routines[rng.Next(routines.Count)];
        if (routine == "run" && far == null) far = x + dmSide * 300 * S;   // asked for by the debug endpoint: the floor's clamped anyway

        switch (routine)
        {
            case "run": AddRun(steps, far!.Value, shift); break;
            case "shoulder": AddShoulder(steps, shift); break;
            case "play": AddPlay(steps, shift); break;
            case "curl": AddCurl(steps); break;
            default: AddPounce(steps); break;
        }
        steps.AddRange(Homeward());

        daemon.Reset();
        daemon.Show();
        SetDaemonSteps(steps);
        Hurry();
        Store.Log($"Daemon: out ({routine}{(shift && routine is "run" or "shoulder" or "play" ? ", shapeshifting" : "")}).");
    }

    /// <summary>A dash along the taskbar and back. Shapeshifting, it comes back as something else, or as each in turn.</summary>
    void AddRun(List<DaemonStep> steps, double far, bool shift)
    {
        steps.Add(Walk(() => (far, y), 230));
        steps.Add(Stay(DaemonPose.Sit, 0.6 + rng.NextDouble() * 0.8).Facing(1));
        (double X, double Y) Back(double u) => (Lerp(Beside(52).X, far, u), y);
        if (!shift) steps.Add(Walk(() => Beside(52), 210));
        else if (rng.NextDouble() < 0.5)
        {
            // The full round, the way daemons do: cat → mouse → moth → bird → (home as a) cat.
            steps.Add(Stay(DaemonPose.Stand, 0.35).Shifting(DaemonForm.Mouse));
            steps.Add(Walk(() => Back(0.66), 170));
            steps.Add(Stay(DaemonPose.Stand, 0.35).Shifting(DaemonForm.Moth));
            steps.Add(Walk(() => Back(0.33), 95));
            steps.Add(Stay(DaemonPose.Stand, 0.35).Shifting(DaemonForm.Bird));
            steps.Add(Walk(() => Beside(52), 130));
        }
        else
        {
            var f = new[] { DaemonForm.Mouse, DaemonForm.Moth, DaemonForm.Bird }[rng.Next(3)];
            steps.Add(Stay(DaemonPose.Stand, 0.35).Shifting(f));
            steps.Add(Walk(() => Beside(52), f == DaemonForm.Mouse ? 170 : f == DaemonForm.Moth ? 95 : 130));
        }
    }

    /// <summary>Up onto his shoulder for a sit and a look round (as a little bird for a bit, shapeshifting), then down.</summary>
    void AddShoulder(List<DaemonStep> steps, bool shift)
    {
        steps.Add(Hop(Shoulder, 0.55, 26));
        steps.Add(Pin(Shoulder, DaemonPose.Sit, 3 + rng.NextDouble() * 3).Facing(-1));
        if (shift)
        {
            steps.Add(Pin(Shoulder, DaemonPose.Sit, 3 + rng.NextDouble() * 2).Facing(-1).Shifting(DaemonForm.Bird));
            steps.Add(Pin(Shoulder, DaemonPose.Sit, 0.6).Facing(-1).Shifting(DaemonForm.Cat));
        }
        steps.Add(Pin(Shoulder, DaemonPose.Sit, 1.5 + rng.NextDouble() * 2).Facing(1));   // a look at him before it goes
        steps.Add(Hop(() => Beside(52), 0.5, 14));
    }

    /// <summary>
    /// Chasing a little glowing mote that flits about beside him: a few pounces that miss, (shapeshifting) a flutter
    /// after it as a moth, then the pounce that catches it.
    /// </summary>
    void AddPlay(List<DaemonStep> steps, bool shift)
    {
        steps.Add(Stay(DaemonPose.Crouch, 1.0).Facing(2).Then(StartMote));
        int misses = 2 + rng.Next(2);
        for (int i = 0; i < misses; i++)
        {
            steps.Add(Hop(MoteFloor, 0.45, 18));
            steps.Add(Stay(DaemonPose.Crouch, 0.5 + rng.NextDouble() * 0.6).Facing(2));
        }
        if (shift)
        {
            steps.Add(Stay(DaemonPose.Stand, 0.35).Shifting(DaemonForm.Moth).Facing(2));
            steps.Add(Walk(MoteFloor, 90, 2.5));
            steps.Add(Stay(DaemonPose.Crouch, 0.6).Shifting(DaemonForm.Cat).Facing(2));
        }
        steps.Add(Hop(MoteFloor, 0.45, 22));
        steps.Add(Stay(DaemonPose.Bat, 1.0).Then(() => moteCaught = now));   // got it
        steps.Add(Stay(DaemonPose.Sit, 0.6).Facing(1).Then(() => dmMote = false));
    }

    /// <summary>A moment curled up beside him, eyes shut, then a stretch.</summary>
    void AddCurl(List<DaemonStep> steps)
    {
        steps.Add(Walk(() => Beside(46), 120));
        steps.Add(Stay(DaemonPose.Curl, 7 + rng.NextDouble() * 6).Facing(1));
        steps.Add(Stay(DaemonPose.Stretch, 1.1).Facing(1));
    }

    /// <summary>Backs off, stalks (that wiggle), pounces on his boots and bats at them; he flinches and looks down.</summary>
    void AddPounce(List<DaemonStep> steps)
    {
        steps.Add(Walk(() => Beside(95), 160));
        steps.Add(Stay(DaemonPose.Crouch, 1.3 + rng.NextDouble() * 0.6).Facing(1));
        steps.Add(Hop(() => (x + dmSide * 26 * S, y), 0.35, 12));
        steps.Add(Stay(DaemonPose.Bat, 1.2).Facing(1).Then(() => { dmPounced = now; squashUntil = now + 0.18; squashAmp = 0.1; }));
        steps.Add(Hop(() => Beside(52), 0.45, 14));
    }

    /// <summary>Back to him (as a cat), a look up, then a hop into his pocket or a quiet fade.</summary>
    List<DaemonStep> Homeward()
    {
        var steps = new List<DaemonStep>
        {
            Walk(() => Beside(52), 170).Shifting(DaemonForm.Cat),
            Stay(DaemonPose.Sit, 0.4).Facing(1),
        };
        steps.Add(rng.NextDouble() < 0.6 ? Hop(Pocket, 0.55, 22).FadeOut() : Stay(DaemonPose.Sit, 0.7).FadeOut());
        return steps;
    }

    // Step makers.
    static DaemonStep Stay(DaemonPose p, double seconds) => new() { Move = DaemonMove.Stay, Pose = p, Seconds = seconds };
    static DaemonStep Walk(Func<(double X, double Y)> to, double speed, double most = 20) =>
        new() { Move = DaemonMove.Walk, Pose = DaemonPose.Sit, To = to, Speed = speed, Seconds = most };
    static DaemonStep Hop(Func<(double X, double Y)> to, double seconds, double height) =>
        new() { Move = DaemonMove.Hop, Pose = DaemonPose.Stand, To = to, Seconds = seconds, Height = height };
    static DaemonStep Pin(Func<(double X, double Y)> to, DaemonPose p, double seconds) =>
        new() { Move = DaemonMove.Pin, Pose = p, To = to, Seconds = seconds, OnHim = true };

    // Where things are, in physical pixels. Up from his feet is minus; Breath.Y (rig units, + is down) lifts and lowers his top half.
    (double X, double Y) Beside(double units) => (x + dmSide * units * S, y);
    (double X, double Y) Pocket() => (x, y - (RigSole - 120 - Breath.Y) * S);                    // just inside the pocket's top
    (double X, double Y) Shoulder() => (x + dmSide * 21 * S, y - (RigSole - 73 - Breath.Y) * S);  // where the hood meets the body
    (double X, double Y) MoteFloor() => (moteX, y);

    /// <summary>
    /// Which side of him it plays on (screen ±1): the free mitten's side if there's room (on his monitor, not under the
    /// window you're in), else the mug's; 0 if neither.
    /// </summary>
    int SideWithRoom(double units)
    {
        var wa = ScreenAt(x).WorkingArea;
        var fg = ForegroundRect();
        bool Ok(int s)
        {
            double px = x + s * units * S;
            if (px < wa.Left + 16 * S || px > wa.Right - 16 * S) return false;
            return fg is not { } r || !(r.Left < px + 18 * S && r.Right > px - 18 * S && r.Top < y && r.Bottom > y - 30 * S);
        }
        int free = -facing;   // the mug's in his right mitten, which is on the +facing side of the screen
        return Ok(free) ? free : Ok(-free) ? -free : 0;
    }

    /// <summary>Somewhere a fair way along the taskbar on his monitor to run to, without crossing the window you're in.</summary>
    double? RunSpot()
    {
        var wa = ScreenAt(x).WorkingArea;
        var fg = ForegroundRect();
        double lo = wa.Left + 30 * S, hi = wa.Right - 30 * S;
        for (int i = 0; i < 8; i++)
        {
            double d = (220 + rng.NextDouble() * 380) * S * (rng.Next(2) == 0 ? -1 : 1);
            double px = x + d;
            if (px < lo || px > hi) px = x - d;
            if (px < lo || px > hi) continue;
            if (fg is { } r && r.Left < Math.Max(px, x) && r.Right > Math.Min(px, x) && r.Top < y && r.Bottom > y - 30 * S) continue;
            return px;
        }
        return null;
    }

    void SetDaemonSteps(List<DaemonStep> steps) { dmSteps = steps; dmIx = -1; NextDaemonStep(); }

    void NextDaemonStep()
    {
        dmIx++; dmStepAt = now; dmFromX = dmX; dmFromY = dmY;
        if (dmIx >= dmSteps.Count) return;
        var s = dmSteps[dmIx];
        if (s.Shift is { } f && f != dmForm) { dmForm = f; daemon?.ShiftTo(f); }
        s.Start?.Invoke();
    }

    /// <summary>Anything that means it should be out of the way right now (it fades straight back in).</summary>
    bool DaemonMustGo() =>
        !Store.Config.Daemon || hidden || autoHidden || chat != null || thinking || currentNudge != null
        || Bubble.Visibility == Visibility.Visible || Knocking || Coding
        || mode is Mode.Dragging or Mode.Falling or Mode.Hopping or Mode.Climbing
        || Services.Hub.Quiet || Services.Hub.Presence.Locked;

    /// <summary>Called every frame (after Animate): moves it along its routine, or sends it home.</summary>
    void UpdateDaemon(double dt)
    {
        if (!daemonOut) return;
        if (!daemonGoing && DaemonMustGo()) { daemonGoing = true; dmGoStart = now; dmGoAlpha = dmAlpha; dmMote = false; }
        if (daemonGoing)
        {
            double u = Smooth((now - dmGoStart) / 0.25);
            dmAlpha = dmGoAlpha * (1 - u); dmSize = 1 - 0.5 * u; dmMoving = false;
            if (u >= 1) DaemonGone();
            return;
        }

        // He's dozed off or gone to lunch: it curls up asleep beside him until he's up again.
        if ((mode == Mode.Sleeping || AtLunch) && !dmAsleep) CurlUpAsleep();
        else if (dmAsleep && mode != Mode.Sleeping && !AtLunch)
        {
            dmAsleep = false; dmHeadingBack = true;
            var steps = new List<DaemonStep> { Stay(DaemonPose.Stretch, 1.1).Facing(1) };
            steps.AddRange(Homeward());
            SetDaemonSteps(steps);
        }
        // He's walked off (dodging a window) mid-game: game over, back to him. On his shoulder it just rides along.
        else if (!dmAsleep && !dmHeadingBack && mode == Mode.Walking && !DaemonOnHim)
        {
            dmHeadingBack = true; dmMote = false;
            SetDaemonSteps(Homeward());
        }

        UpdateMote(dt);
        RunDaemonStep(dt);
        if (!daemonOut) return;
        if (!DaemonOnHim)
        {
            // Kept on his monitor's work area, standing on its floor at the lowest.
            var wa = ScreenAt(x).WorkingArea;
            dmX = Math.Clamp(dmX, wa.Left + 14 * S, Math.Max(wa.Left + 14 * S, wa.Right - 14 * S));
            dmY = Math.Min(dmY, wa.Bottom);
        }
    }

    /// <summary>To his side (keeping up if he's still walking to his lunch spot), as a cat, then curled up asleep.</summary>
    void CurlUpAsleep()
    {
        dmAsleep = true; dmMote = false; dmSideAt = -99;
        var steps = new List<DaemonStep>();
        if (DaemonOnHim) steps.Add(Hop(SleepSpot, 0.5, 12));
        var walk = Walk(SleepSpot, 200, 120).Shifting(DaemonForm.Cat);
        walk.Follow = true;
        steps.Add(walk);
        steps.Add(Stay(DaemonPose.Curl, 1.2).Facing(1));
        steps.Add(Stay(DaemonPose.Sleep, double.PositiveInfinity).Facing(1));
        SetDaemonSteps(steps);
    }

    /// <summary>Beside him to sleep, on whichever side has room (re-checked now and then, as he may still be walking to a corner).</summary>
    (double X, double Y) SleepSpot()
    {
        if (now - dmSideAt > 1) { dmSideAt = now; int s = SideWithRoom(50); if (s != 0) dmSide = s; }
        return Beside(dmSide == -facing ? 50 : 75);   // on the mug's side (all there's room for in a corner) it lies past the mug
    }

    void RunDaemonStep(double dt)
    {
        if (dmIx < 0 || dmIx >= dmSteps.Count) { DaemonGone(); return; }
        var s = dmSteps[dmIx];
        double t = now - dmStepAt;
        double u = s.Seconds > 0 && !double.IsInfinity(s.Seconds) ? Math.Clamp(t / s.Seconds, 0, 1) : 0;
        bool done;
        dmMoving = false; dmHop = 0; dmPose = s.Pose;
        switch (s.Move)
        {
            case DaemonMove.Walk:
                var (wx, wy) = s.To!();
                double step = s.Speed * S * dt, dx = wx - dmX;
                dmY = wy;
                if (Math.Abs(dx) <= Math.Max(step, 0.5))
                {
                    dmX = wx;
                    done = !(s.Follow && mode == Mode.Walking);   // keeping up with him: there when he stops
                    if (Math.Abs(x - dmX) > 2) dmFacing = Math.Sign(x - dmX);
                }
                else { dmX += Math.Sign(dx) * step; dmFacing = Math.Sign(dx); dmMoving = true; dmPose = DaemonPose.Run; done = false; }
                if (t >= s.Seconds) done = true;
                break;
            case DaemonMove.Hop:
                var (hx, hy) = s.To!();
                dmX = Lerp(dmFromX, hx, u); dmY = Lerp(dmFromY, hy, u);
                dmHop = s.Height * 4 * u * (1 - u);
                if (Math.Abs(hx - dmFromX) > 1) dmFacing = Math.Sign(hx - dmFromX);
                dmMoving = true; done = u >= 1;
                break;
            case DaemonMove.Pin:
                (dmX, dmY) = s.To!();
                done = t >= s.Seconds;
                break;
            default:
                done = t >= s.Seconds;
                break;
        }
        if (s.Face != 0 && s.Move is DaemonMove.Stay or DaemonMove.Pin)
        {
            double d = s.Face == 2 ? moteX - dmX : (x - dmX) * s.Face;
            if (Math.Abs(d) > 2) dmFacing = Math.Sign(d);
        }
        dmAlpha = s.In ? Smooth(u) : s.Out ? 1 - Smooth((u - 0.45) / 0.55) : 1;
        dmSize = s.In ? 0.35 + 0.65 * Smooth(u) : s.Out ? 1 - 0.6 * Smooth((u - 0.3) / 0.7) : 1;
        if (done) NextDaemonStep();
    }

    /// <summary>Back in (or faded away): window hidden, next visit worked out, and he gets on with being bored.</summary>
    void DaemonGone()
    {
        daemonOut = daemonGoing = dmMote = dmMoving = false;
        dmIx = -1; dmSteps = new();
        daemon?.Hide();
        ScheduleDaemon();
        nextAct = Math.Max(nextAct, now + 8 + rng.NextDouble() * 15);
    }

    /// <summary>The mote starts out just in front of it, flitting about a spot beside him.</summary>
    void StartMote()
    {
        dmMote = true; moteCaught = -1;
        moteCx = Beside(80).X;
        moteX = dmX + dmFacing * 26 * S; moteH = 22; nextMoteMove = now;
    }

    /// <summary>Drifts to a new spot every second or so, low enough to pounce at; once caught it fades out.</summary>
    void UpdateMote(double dt)
    {
        if (!dmMote) return;
        if (moteCaught >= 0)
        {
            if (now - moteCaught > 0.5) dmMote = false;
            return;
        }
        if (now >= nextMoteMove)
        {
            moteToX = moteCx + (rng.NextDouble() * 2 - 1) * 60 * S;
            moteToH = 8 + rng.NextDouble() * 34;
            nextMoteMove = now + 0.7 + rng.NextDouble() * 0.8;
        }
        double k = Math.Min(1, dt * 2.5);
        moteX += (moteToX - moteX) * k; moteH += (moteToH - moteH) * k;
    }

    /// <summary>Clicked while sitting still: a hop and a happy wiggle (asleep, just a sleepy look). He glances over.</summary>
    void PokeDaemon()
    {
        if (!daemonOut || daemonGoing) return;
        dmPokeAt = now;
        if (mode != Mode.Sleeping) blinkStart = now;
        Hurry();
    }

    /// <summary>Where he looks while it's out: following it about, down at his boots after a pounce, round at his shoulder.</summary>
    (double Turn, double LookY)? DaemonGaze()
    {
        if (!daemonOut || daemonGoing || mode != Mode.Idle) return null;
        if (DaemonOnHim && dmIx < dmSteps.Count && dmSteps[dmIx].Move == DaemonMove.Pin && dmPose == DaemonPose.Sit && dmY < y - 40 * S)
            return (28 * dmSide * facing, 0.5);   // on his shoulder
        double up = (y - dmY) / S + dmHop;
        double turn = Math.Clamp((dmX - x) / (220 * S), -1, 1) * 34 * facing;
        double look = now - dmPounced < 1.4 ? 3.5 : Math.Clamp(3.4 - up / 20, -2.5, 3.5);
        return (turn, look);
    }

    /// <summary>Called every frame after his own window is placed: puts it where it is, drawn as it is, over him.</summary>
    void PlaceDaemon()
    {
        if (daemon == null || !daemonOut) return;
        bool raise = nextTopmost != dmRaisedFor;   // he's just taken the top spot back (every 2 s): go back over him
        dmRaisedFor = nextTopmost;
        daemon.Place(dmX, dmY, raise);
        // Click-through while it moves or fades, so it never catches a click meant for what's underneath.
        daemon.ClickThrough = daemonGoing || dmMoving || dmAlpha < 0.9;
        (double X, double Y, double A)? mote = null;
        if (dmMote)
        {
            double fade = moteCaught >= 0 ? 1 - Math.Clamp((now - moteCaught) / 0.5, 0, 1) : 1;
            mote = (moteX + Math.Sin(now * 5.3) * 2 * S, y - (moteH + Math.Sin(now * 3.7) * 3) * S, fade);
        }
        daemon.Draw(now, dmPose, dmMoving, dmFacing, dmHop, dmSize, dmAlpha, now - dmPokeAt, mote);
    }
}

/// <summary>
/// The daemon's own little window: borderless, see-through, topmost, never focused, not in Alt-Tab, kept out of screen
/// captures like Vex. Only its drawn shapes can be clicked, and the whole window is click-through (WS_EX_TRANSPARENT)
/// whenever VexWindow says so (while it moves). Drawn in his style: a soft dark silhouette with a faint rim in a pale
/// shade of his hoodie colour (so it reads on dark backgrounds as well as light ones) and small glowing white eyes.
/// </summary>
sealed class DaemonWindow : Window
{
    /// <summary>Window size in DIPs, and where its feet are in it: room above for hops, flight and the mote.</summary>
    public const double W = 170, H = 130, FeetX = 85, FeetY = 122;

    readonly Canvas pet = new() { Cursor = Cursors.Hand };
    readonly ScaleTransform wiggle = new(), flip = new(), grow = new() { CenterY = -8 };
    readonly TranslateTransform rise = new();
    readonly Ellipse puff;
    readonly ScaleTransform puffSize = new() { CenterX = 13, CenterY = 13 };
    readonly Canvas mote = new() { IsHitTestVisible = false, Opacity = 0 };
    readonly TranslateTransform moteAt = new();
    readonly DaemonPaint paint = new();
    readonly Dictionary<DaemonForm, DaemonLook> looks = new();
    readonly Random rng = new();
    DaemonForm form = DaemonForm.Cat, shiftFrom = DaemonForm.Cat;
    double shiftAt = -99, last = -1, gait, nextBlink, blinkAt = -1, k = 1;
    IntPtr hwnd;
    bool through = true;
    int lastLeft = int.MinValue, lastTop;

    /// <summary>Mid-shapeshift (for the frame rate).</summary>
    public bool Shifting => last - shiftAt < ShiftSeconds;
    const double ShiftSeconds = 0.5;

    public DaemonWindow(Action poked)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Width = W; Height = H; Left = -32000; Top = -32000; Title = "Vex daemon"; Focusable = false;

        looks[DaemonForm.Cat] = new CatLook(paint);
        looks[DaemonForm.Mouse] = new MouseLook(paint);
        looks[DaemonForm.Moth] = new MothLook(paint);
        looks[DaemonForm.Bird] = new BirdLook(paint);
        foreach (var l in looks.Values) pet.Children.Add(l.Root);

        // The shapeshift puff: a soft ring that blooms and fades while one form shrinks away and the next grows.
        puff = new Ellipse
        {
            Width = 26, Height = 26, Stroke = paint.Rim, StrokeThickness = 1.2, Opacity = 0, IsHitTestVisible = false,
            Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF))),
            RenderTransform = puffSize,
        };
        Canvas.SetLeft(puff, -13); Canvas.SetTop(puff, -25);
        pet.Children.Add(puff);
        pet.RenderTransform = new TransformGroup { Children = { wiggle, flip, grow, rise, new TranslateTransform(FeetX, FeetY) } };

        // The mote it chases: a tiny glowing speck.
        var halo = new Ellipse { Width = 12, Height = 12, Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xF4), Color.FromArgb(0, 0xFF, 0xFF, 0xF4))) };
        Canvas.SetLeft(halo, -6); Canvas.SetTop(halo, -6);
        var core = new Ellipse { Width = 3, Height = 3, Fill = Brushes.White };
        Canvas.SetLeft(core, -1.5); Canvas.SetTop(core, -1.5);
        mote.Children.Add(halo); mote.Children.Add(core);
        mote.RenderTransform = moteAt;

        var root = new Canvas { Width = W, Height = H };
        root.Children.Add(pet);
        root.Children.Add(mote);
        Content = root;

        pet.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pet.MouseLeftButtonUp += (_, e) => { poked(); e.Handled = true; };
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
            if (Store.Config.HideFromScreenCapture) Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        Theme.Changed += Recolour;
        Recolour();
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>The rim: his hoodie colour taken most of the way to white, half see-through. Follows Theme changes.</summary>
    void Recolour()
    {
        var c = Theme.Hoodie;
        byte Pale(byte v) => (byte)(v + (255 - v) * 0.5);
        paint.Rim.Color = Color.FromArgb(0x80, Pale(c.R), Pale(c.G), Pale(c.B));
    }

    /// <summary>Ready to come out: a cat, invisible until the first frame fades it in, placed afresh.</summary>
    public void Reset()
    {
        form = shiftFrom = DaemonForm.Cat; shiftAt = -99; last = -1;
        pet.Opacity = 0; mote.Opacity = 0;
        lastLeft = int.MinValue;
    }

    /// <summary>Starts a shapeshift into <paramref name="to"/>.</summary>
    public void ShiftTo(DaemonForm to)
    {
        if (to == form) return;
        shiftFrom = form; form = to; shiftAt = last;
    }

    /// <summary>Whole window click-through (moving), or only its see-through parts (sitting still: it can be clicked).</summary>
    public bool ClickThrough
    {
        set
        {
            if (value == through || hwnd == IntPtr.Zero) return;
            through = value;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, value ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT);
        }
    }

    /// <summary>Puts its feet at (fx, fy), physical pixels; <paramref name="raise"/> also puts it back on top.</summary>
    public void Place(double fx, double fy, bool raise)
    {
        if (hwnd == IntPtr.Zero) return;
        try { k = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        if (k <= 0) k = 1;
        int left = (int)Math.Round(fx - FeetX * k), top = (int)Math.Round(fy - FeetY * k);
        if (!raise && left == lastLeft && top == lastTop) return;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        lastLeft = left; lastTop = top;
    }

    /// <summary>
    /// One frame. <paramref name="hop"/> is how high it is in a hop (DIPs); <paramref name="poke"/> is seconds since
    /// it was clicked; <paramref name="moteAtWorld"/> is where the mote is (physical pixels) and its opacity, if out.
    /// </summary>
    public void Draw(double t, DaemonPose pose, bool moving, int facing, double hop, double size, double alpha, double poke,
                     (double X, double Y, double A)? moteAtWorld)
    {
        double dt = last < 0 ? 0 : Math.Min(0.1, t - last);
        last = t;
        if (moving) gait += dt * form switch { DaemonForm.Mouse => 26, DaemonForm.Bird => 9, DaemonForm.Moth => 0, _ => 13 };

        // Blinks now and then; a poke opens sleepy eyes for a moment.
        double blink = 1;
        if (blinkAt < 0 && t >= nextBlink) blinkAt = t;
        if (blinkAt >= 0)
        {
            double b = (t - blinkAt) / 0.16;
            if (b >= 1) { blinkAt = -1; nextBlink = t + 1.8 + rng.NextDouble() * 4; }
            else blink = Math.Abs(1 - 2 * b) * 0.9 + 0.1;
        }
        bool asleep = pose is DaemonPose.Curl or DaemonPose.Sleep;
        bool roused = asleep && poke < 0.9;

        // Shapeshift: the old form shrinks to nothing, a puff blooms, the new one grows out of it.
        double s = (t - shiftAt) / ShiftSeconds;
        foreach (var kv in looks)
        {
            bool showing = kv.Key == form || (s < 1 && kv.Key == shiftFrom);
            kv.Value.Root.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        }
        double lift = looks[form].Pose(pose, t, dt, gait, blink, roused);
        if (s < 1)
        {
            looks[shiftFrom].Pose(pose, t, dt, gait, blink, roused);
            double g = DaemonLook.Smooth(s / 0.45);
            looks[shiftFrom].Pop.ScaleX = looks[shiftFrom].Pop.ScaleY = 1 - g;
            looks[form].Pop.ScaleX = looks[form].Pop.ScaleY = DaemonLook.Smooth((s - 0.4) / 0.6);
            puff.Opacity = 0.9 * Math.Sin(Math.PI * Math.Clamp(s, 0, 1));
            puffSize.ScaleX = puffSize.ScaleY = 0.3 + 1.2 * s;
        }
        else
        {
            looks[form].Pop.ScaleX = looks[form].Pop.ScaleY = 1;
            puff.Opacity = 0;
        }

        // A poke: a little hop and a happy wiggle (asleep, it just looks up).
        double pokeHop = !asleep && poke < 0.45 ? 9 * Math.Sin(Math.PI * poke / 0.45) : 0;
        wiggle.ScaleX = !asleep && poke < 0.8 ? 1 + 0.12 * Math.Sin(poke * 40) * (1 - poke / 0.8) : 1;
        flip.ScaleX = facing < 0 ? -1 : 1;
        grow.ScaleX = grow.ScaleY = size;
        rise.Y = -(hop + lift + pokeHop);
        pet.Opacity = alpha;

        if (moteAtWorld is { } m && lastLeft != int.MinValue)
        {
            moteAt.X = Math.Clamp((m.X - lastLeft) / k, 4, W - 4);
            moteAt.Y = Math.Clamp((m.Y - lastTop) / k, 4, H - 4);
            mote.Opacity = alpha * m.A * (0.75 + 0.25 * Math.Sin(t * 9));   // twinkles
        }
        else mote.Opacity = 0;
    }
}

/// <summary>The daemon's paint box: its dark fill, the hoodie-tinted rim (one brush, recoloured live) and glowing eyes.</summary>
sealed class DaemonPaint
{
    public readonly Brush Fill, Strand;
    public readonly SolidColorBrush Rim = new();
    static readonly Brush Glow = MakeGlow();

    public DaemonPaint()
    {
        // Soft and dark, a touch lighter on top, like the matte fabric of his hood.
        var f = new LinearGradientBrush(Color.FromRgb(0x36, 0x38, 0x42), Color.FromRgb(0x16, 0x17, 0x1C), 90);
        f.Freeze(); Fill = f;
        var s = new SolidColorBrush(Color.FromRgb(0x22, 0x23, 0x2A));
        s.Freeze(); Strand = s;
    }

    static Brush MakeGlow()
    {
        var g = new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF));
        g.Freeze();
        return g;
    }

    public Path Part(string data) => new()
    {
        Data = Geometry.Parse(data), Fill = Fill, Stroke = Rim, StrokeThickness = 0.7, StrokeLineJoin = PenLineJoin.Round,
    };

    public Ellipse Oval(double left, double top, double w, double h)
    {
        var e = new Ellipse { Width = w, Height = h, Fill = Fill, Stroke = Rim, StrokeThickness = 0.7 };
        Canvas.SetLeft(e, left); Canvas.SetTop(e, top);
        return e;
    }

    public Rectangle Leg(double left, double top, double w, double h)
    {
        var r = new Rectangle { Width = w, Height = h, RadiusX = w / 2, RadiusY = w / 2, Fill = Fill, Stroke = Rim, StrokeThickness = 0.6 };
        Canvas.SetLeft(r, left); Canvas.SetTop(r, top);
        return r;
    }

    /// <summary>A tail, antennae or legs: a thin dark line with the rim showing along both sides.</summary>
    public Canvas Line(string data, double width)
    {
        var g = Geometry.Parse(data);
        var c = new Canvas();
        c.Children.Add(new Path { Data = g, Stroke = Rim, StrokeThickness = width + 1.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        c.Children.Add(new Path { Data = g, Stroke = Strand, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return c;
    }

    /// <summary>Small white eyes with a soft glow, as in his hood (a gradient halo rather than a blur, which costs far more).</summary>
    public Canvas Eyes(ScaleTransform blink, double w, double h, params (double X, double Y)[] at)
    {
        var c = new Canvas { RenderTransform = blink, IsHitTestVisible = false };
        foreach (var (ex, ey) in at)
        {
            var halo = new Ellipse { Width = w * 3.4, Height = h * 2.6, Fill = Glow };
            Canvas.SetLeft(halo, ex - w * 1.7); Canvas.SetTop(halo, ey - h * 1.3);
            var eye = new Ellipse { Width = w, Height = h, Fill = Brushes.White };
            Canvas.SetLeft(eye, ex - w / 2); Canvas.SetTop(eye, ey - h / 2);
            c.Children.Add(halo); c.Children.Add(eye);
        }
        return c;
    }
}

/// <summary>
/// One of the daemon's forms, drawn facing right with its feet at (0, 0), in DIPs (a cat is about 30 long). Pose moves
/// its parts for this frame and says how far off the ground it is (flying), in DIPs.
/// </summary>
abstract class DaemonLook
{
    public readonly Canvas Root = new();
    /// <summary>Its size during a shapeshift (0 to 1).</summary>
    public readonly ScaleTransform Pop = new() { CenterY = -8 };

    protected DaemonLook() { Root.RenderTransform = Pop; }

    public abstract double Pose(DaemonPose pose, double t, double dt, double gait, double blink, bool roused);

    /// <summary>Eases a value towards where it's going, so poses blend into each other rather than snap.</summary>
    protected static double Ease(double v, double to, double dt, double rate = 9) => v + (to - v) * Math.Min(1, dt * rate);

    public static double Smooth(double u) { u = Math.Clamp(u, 0, 1); return u * u * (3 - 2 * u); }
}

/// <summary>The cat: its settled form. Body, head with ears, four little legs and a tail, each moved per pose.</summary>
sealed class CatLook : DaemonLook
{
    readonly ScaleTransform bodyS = new() { CenterX = -11, CenterY = -5.5 };
    readonly RotateTransform bodyR = new() { CenterX = -11, CenterY = -5.5 };
    readonly TranslateTransform headT = new(), frontT = new();
    readonly ScaleTransform frontS = new(), blinkS = new() { CenterY = -19.6 };
    readonly RotateTransform tailR = new() { CenterX = -11, CenterY = -11 };
    readonly TranslateTransform[] legs = { new(), new(), new(), new() };   // back, back, front, front
    readonly Canvas back = new(), front = new();

    // Per pose: body angle, body scale x, y, head x, y, front legs x, front legs height, front legs shown, back legs shown, tail angle, eyes open.
    static readonly double[] StandP = { 0, 1, 1, 0, 0, 0, 1, 1, 1, -5, 1 };
    static readonly double[] RunP = { 0, 1.08, 0.94, 1.5, 1, 0, 1, 1, 1, -40, 1 };
    static readonly double[] SitP = { -38, 1, 1, -6, -10, -5, 1.8, 1, 1, -88, 1 };
    static readonly double[] CrouchP = { 0, 1.05, 0.8, 3, 5, 1, 0.8, 1, 1, -20, 1 };
    static readonly double[] CurlP = { 0, 0.78, 1.05, -6, 9, 0, 1, 0, 0, -100, 0.12 };
    static readonly double[] SleepP = { 0, 0.78, 1.05, -6, 9, 0, 1, 0, 0, -100, 0 };
    static readonly double[] StretchP = { 12, 1.18, 0.9, 5, 6, 3, 1, 1, 1, -10, 0.35 };
    readonly double[] cur = (double[])StandP.Clone();

    public CatLook(DaemonPaint p)
    {
        var tail = p.Line("M -11,-11 C -17,-12 -20,-18 -18,-25", 2.8);
        tail.RenderTransform = tailR;
        back.Children.Add(Leg(p, -10.5, 0)); back.Children.Add(Leg(p, -6.5, 1));
        front.Children.Add(Leg(p, 4.5, 2)); front.Children.Add(Leg(p, 8.5, 3));
        front.RenderTransform = new TransformGroup { Children = { frontS, frontT } };
        var body = p.Part("M -13,-10 C -13,-15.5 -6,-16.5 1,-16.5 C 8,-16.5 12,-14 12,-10 C 12,-6 8,-5.5 0,-5.5 C -8,-5.5 -13,-6 -13,-10 Z");
        body.RenderTransform = new TransformGroup { Children = { bodyS, bodyR } };
        var head = new Canvas { RenderTransform = headT };
        head.Children.Add(p.Part("M 7.5,-20.5 L 8.2,-28 L 12,-23 Z M 13,-23.5 L 16.8,-28 L 17.6,-20 Z"));   // ears
        head.Children.Add(p.Oval(6, -24, 12.5, 11));
        head.Children.Add(p.Eyes(blinkS, 1.7, 2.4, (13.2, -19.6), (16.4, -19.6)));
        foreach (var e in new UIElement[] { tail, back, body, front, head }) Root.Children.Add(e);
    }

    Rectangle Leg(DaemonPaint p, double left, int i) { var r = p.Leg(left, -8, 3.2, 8); r.RenderTransform = legs[i]; return r; }

    public override double Pose(DaemonPose pose, double t, double dt, double gait, double blink, bool roused)
    {
        var target = pose switch
        {
            DaemonPose.Run => RunP, DaemonPose.Sit or DaemonPose.Bat => SitP, DaemonPose.Crouch => CrouchP,
            DaemonPose.Curl => CurlP, DaemonPose.Sleep => SleepP, DaemonPose.Stretch => StretchP, _ => StandP,
        };
        for (int i = 0; i < cur.Length; i++) cur[i] = Ease(cur[i], target[i], dt);
        bool curled = pose is DaemonPose.Curl or DaemonPose.Sleep, run = pose == DaemonPose.Run;

        double breathe = curled ? 0.05 * Math.Sin(t * 1.3) : 0.02 * Math.Sin(t * 2.4);
        bodyR.Angle = cur[0] + (pose == DaemonPose.Crouch ? 3 * Math.Sin(t * 16) : 0);   // the wiggle before a pounce
        bodyS.ScaleX = cur[1]; bodyS.ScaleY = cur[2] * (1 + breathe);
        headT.X = cur[3];
        headT.Y = cur[4] + (run ? 0.6 * Math.Sin(gait * 2) : curled ? 0.5 * Math.Sin(t * 1.3) : 0) - (roused ? 2.5 : 0);
        frontT.X = cur[5]; frontS.ScaleY = cur[6]; front.Opacity = cur[7]; back.Opacity = cur[8];
        tailR.Angle = cur[9] + (run ? 8 * Math.Sin(gait * 2) : curled ? 2 * Math.Sin(t * 0.7) : 9 * Math.Sin(t * 1.7));

        // Trotting: diagonal pairs together. Batting: one front paw up and down.
        double g = Math.Sin(gait), bat = pose == DaemonPose.Bat ? Math.Max(0, Math.Sin(t * 7)) : 0;
        legs[0].X = run ? 2.4 * g : 0; legs[0].Y = run ? -1.5 * Math.Max(0, g) : 0;
        legs[1].X = run ? -2.4 * g : 0; legs[1].Y = run ? -1.5 * Math.Max(0, -g) : 0;
        legs[2].X = run ? -2.4 * g : 0; legs[2].Y = run ? -1.5 * Math.Max(0, -g) : 0;
        legs[3].X = run ? 2.4 * g : 2.5 * bat; legs[3].Y = run ? -1.5 * Math.Max(0, g) : -5 * bat;

        blinkS.ScaleY = roused ? 0.6 : Math.Min(Math.Max(cur[10], 0.06), blink);
        return 0;
    }
}

/// <summary>The mouse: low and quick, a round ear and a long thin tail. Scurries with a fast little bob.</summary>
sealed class MouseLook : DaemonLook
{
    readonly TranslateTransform bob = new();
    readonly ScaleTransform sit = new() { CenterX = -2 }, blinkS = new() { CenterY = -6 };
    readonly RotateTransform tailR = new() { CenterX = -8, CenterY = -3 };
    double sitK, eye = 1;

    public MouseLook(DaemonPaint p)
    {
        var g = new Canvas { RenderTransform = new TransformGroup { Children = { sit, bob } } };
        var tail = p.Line("M -8,-3 C -13,-1 -16,-6 -22,-4", 1.1);
        tail.RenderTransform = tailR;
        g.Children.Add(tail);
        g.Children.Add(p.Part("M -8,-4 C -8,-8 -3,-9.5 2,-9 C 6,-8.5 9,-6 10.5,-3 C 7.5,-1.2 3,-0.6 -2,-0.6 C -6,-0.6 -8,-1.8 -8,-4 Z"));
        g.Children.Add(p.Oval(0.5, -12.5, 5, 5));   // ear
        g.Children.Add(p.Eyes(blinkS, 1.1, 1.5, (5.6, -6.2), (7.3, -5.8)));
        Root.Children.Add(g);
    }

    public override double Pose(DaemonPose pose, double t, double dt, double gait, double blink, bool roused)
    {
        bool run = pose == DaemonPose.Run;
        sitK = Ease(sitK, pose is DaemonPose.Sit or DaemonPose.Bat ? 1 : 0, dt);
        eye = Ease(eye, pose == DaemonPose.Sleep && !roused ? 0 : 1, dt);
        bob.Y = run ? -0.9 * Math.Abs(Math.Sin(gait)) : 0;
        bob.X = run ? 0 : 0.3 * Math.Sin(t * 23) * Math.Max(0, Math.Sin(t * 1.9));   // a sniff now and then
        sit.ScaleY = 1 + 0.15 * sitK; sit.ScaleX = 1 - 0.1 * sitK;
        tailR.Angle = run ? 10 * Math.Sin(gait * 0.5) : 5 * Math.Sin(t * 1.4);
        blinkS.ScaleY = Math.Min(Math.Max(eye, 0.06), blink);
        return 0;
    }
}

/// <summary>The moth: seen from the front, wings fluttering, hovering a little off the ground; settled, wings at rest.</summary>
sealed class MothLook : DaemonLook
{
    readonly ScaleTransform wingL = new(), wingR = new(), blinkS = new() { CenterY = -12.2 };
    double lift, eye = 1;

    public MothLook(DaemonPaint p)
    {
        var l = p.Part("M -0.5,-12 C -6,-20 -13,-18 -12,-11 C -11,-6 -5,-6 -0.5,-9 Z M -0.5,-9 C -5,-8 -9,-4 -6,-2.5 C -3.5,-1.5 -1.5,-5 -0.5,-7 Z");
        var r = p.Part("M 0.5,-12 C 6,-20 13,-18 12,-11 C 11,-6 5,-6 0.5,-9 Z M 0.5,-9 C 5,-8 9,-4 6,-2.5 C 3.5,-1.5 1.5,-5 0.5,-7 Z");
        l.RenderTransform = wingL; r.RenderTransform = wingR;
        Root.Children.Add(l); Root.Children.Add(r);
        Root.Children.Add(p.Oval(-2, -14, 4, 11));   // body
        Root.Children.Add(p.Line("M -0.6,-13.5 C -1.5,-16.5 -3,-18 -4.5,-18.2 M 0.6,-13.5 C 1.5,-16.5 3,-18 4.5,-18.2", 0.5));   // antennae
        Root.Children.Add(p.Eyes(blinkS, 1.1, 1.3, (-0.9, -12.2), (0.9, -12.2)));
    }

    public override double Pose(DaemonPose pose, double t, double dt, double gait, double blink, bool roused)
    {
        bool flying = pose is not (DaemonPose.Sit or DaemonPose.Curl or DaemonPose.Sleep);
        lift = Ease(lift, flying ? 16 + 3 * Math.Sin(t * 3.1) : 0, dt, 5);
        double flap = flying ? 0.25 + 0.75 * Math.Abs(Math.Cos(t * 16)) : 0.75 + 0.2 * Math.Cos(t * 1.5);
        wingL.ScaleX = wingR.ScaleX = flap;
        eye = Ease(eye, pose == DaemonPose.Sleep && !roused ? 0 : 1, dt);
        blinkS.ScaleY = Math.Min(Math.Max(eye, 0.06), blink);
        return lift;
    }
}

/// <summary>The little bird: round body, beak, tail; gets about in hops with a flick of the wing, bobs its head when still.</summary>
sealed class BirdLook : DaemonLook
{
    readonly RotateTransform wingR = new() { CenterX = -4, CenterY = -8.5 }, tailR = new() { CenterX = -7, CenterY = -6 };
    readonly TranslateTransform headT = new();
    readonly ScaleTransform blinkS = new() { CenterY = -14.5 };
    double eye = 1;

    public BirdLook(DaemonPaint p)
    {
        var tail = p.Part("M -6.5,-6.5 L -14,-10 L -13,-5.5 Z");
        tail.RenderTransform = tailR;
        var wing = p.Part("M -5,-9 C -1,-11.5 4,-10.5 5.5,-7.5 C 1,-5.5 -4,-5.5 -6,-7.5 Z");
        wing.RenderTransform = wingR;
        var head = new Canvas { RenderTransform = headT };
        head.Children.Add(p.Part("M 10.6,-14.6 L 13.8,-13.6 L 10.6,-12.4 Z"));   // beak
        head.Children.Add(p.Oval(3, -17.5, 8, 7.5));
        head.Children.Add(p.Eyes(blinkS, 1.2, 1.5, (7.9, -14.6), (9.4, -14.4)));
        Root.Children.Add(tail);
        Root.Children.Add(p.Line("M -1,-3 L -1.5,0 M 2,-3 L 2.5,0", 0.8));   // legs
        Root.Children.Add(p.Part("M -8,-6 C -8,-11 -3,-13 2,-12.5 C 6,-12 8,-10 8,-7 C 8,-4 4,-2.5 -1,-2.5 C -5,-2.5 -8,-3.5 -8,-6 Z"));
        Root.Children.Add(wing);
        Root.Children.Add(head);
    }

    public override double Pose(DaemonPose pose, double t, double dt, double gait, double blink, bool roused)
    {
        bool hopping = pose == DaemonPose.Run;
        double h = Math.Abs(Math.Sin(gait));
        wingR.Angle = hopping ? -35 * h : 0;
        tailR.Angle = hopping ? 0 : 8 * Math.Max(0, Math.Sin(t * 2.7)) * Math.Max(0, Math.Sin(t * 0.9));   // the odd tail flick
        headT.Y = hopping ? 0 : 0.7 * Math.Max(0, Math.Sin(t * 3.3));                                   // little head bobs
        eye = Ease(eye, pose == DaemonPose.Sleep && !roused ? 0 : 1, dt);
        blinkS.ScaleY = Math.Min(Math.Max(eye, 0.06), blink);
        return hopping ? 5 * h : 0;
    }
}
