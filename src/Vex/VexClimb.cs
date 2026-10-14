using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// Climbing, one of his idle activities. Bored on the taskbar, he walks to the left or right edge of a window on his
/// monitor, turns side-on to it and climbs hand over hand (mittens gripping, boots against the edge, mug hooked on his
/// back so its level carries on as normal), then pulls himself over onto the window's top and is perched there as if
/// he'd been dropped on it. Perched, the same act takes him back down one of his window's sides: to the taskbar if
/// the edge reaches near enough, else he climbs to its bottom and drops the rest (landing wherever falling lands him).
/// Only window positions and sizes are looked at (Native.TopWindows), never their contents or titles. A route needs
/// an edge at least ClimbMinEdgePixels long, room beside it on his monitor, and nothing stacked above that window
/// over the edge, his way up or his spot on top. Anything going wrong mid-climb (the window moves, closes, minimises,
/// maximises or gets covered) and he lets go: a short fall and a flat, unimpressed look. You clicking him, opening the
/// chat, or any bubble or nudge appearing also brings him straight down, quietly. If window positions can't be read
/// at all, he just doesn't climb.
/// </summary>
public partial class VexWindow
{
    const double ClimbGap = 48;       // DIPs from his centre to the edge he's on: just clear of his body, so dodging never sees it as "under" the window
    const double ClimbHang = 112;     // his feet are this far (DIPs) below his mittens when he hangs from a top edge
    const double ClimbStroke = 44;    // how far (rig units) one pull lifts him; then the other mitten takes over
    const double GripHigh = 34, GripLow = 78, WallX = 110;   // where the mittens hold the edge, in rig units
    const double GrabTime = 0.5, MantleTime = 0.8, StepOverTime = 0.8, ReleaseTime = 0.4;

    enum ClimbStage { Grab, Climb, Mantle, StepOver, Release }

    /// <summary>
    /// One way up or down a window's side. Side is +1 for its left edge (the edge is on his right, so he faces right)
    /// and -1 for its right edge. ColumnX is where he climbs (just outside the edge), LandX where he stands on top.
    /// </summary>
    record ClimbPlan(IntPtr Hwnd, Native.RECT Rect, int Side, bool Up, double ColumnX, double LandX);

    ClimbPlan? climb;                 // the route he's walking to or on
    ClimbStage climbStage;
    double climbT, climbStageAt;      // time on this climb (paused while you hover over him), and when this stage began
    double climbDist;                 // rig units climbed so far, which drives the hand-over-hand cycle
    double climbFromY, climbToY, nextClimbCheck;
    bool landQuiet, landSlip;         // the next landing is after letting go (no "Rude."), and whether it was a slip (unimpressed)
    double unimpressedAt = -99;

    /// <summary>Climbing is on in Setup, and nothing's wanting him: no chat, bubble or nudge, no lunch or laptop.</summary>
    bool ClimbAllowed() => IdleActs.Enabled(Act.Climb) && chat == null && Bubble.Visibility != Visibility.Visible
                           && currentNudge == null && !AtLunch && !Coding;

    /// <summary>A route from where he is now (up a window from the taskbar, or down from his perch), or null if there's none.</summary>
    ClimbPlan? ClimbRoute(Forms.Screen scr)
    {
        // One of the nearer few, so he doesn't always pick the same window but doesn't trek across the screen either.
        double From(ClimbPlan p) => Math.Abs((p.Up ? p.ColumnX : p.LandX) - x);
        var near = ClimbRoutes(scr, IntPtr.Zero).OrderBy(From).Take(3).ToList();
        return near.Count == 0 ? null : near[rng.Next(near.Count)];
    }

    /// <summary>Every usable route on this monitor (or just those on one window). Empty if window positions can't be read.</summary>
    List<ClimbPlan> ClimbRoutes(Forms.Screen scr, IntPtr only)
    {
        var routes = new List<ClimbPlan>();
        try
        {
            var wins = Native.TopWindows(ownPid);
            var wa = scr.WorkingArea;
            double half = BodyHalfW * S, hang = ClimbHang * S;
            double minEdge = Math.Max(1, Store.Config.ClimbMinEdgePixels);
            var fg = Perched ? null : ForegroundRect();
            for (int i = 0; i < wins.Count; i++)
            {
                var (h, r) = wins[i];
                if (only != IntPtr.Zero && h != only) continue;
                if (Perched != (h == perchHwnd)) continue;   // perched: down his own window's sides; on the taskbar: up any other
                if (Native.IsZoomed(h) || IgnoredClasses.Contains(Native.ClassName(h))) continue;
                if (r.Right - r.Left < 2 * (half + 8 * S) + 20 * S) continue;               // too narrow to stand on
                if (r.Top - BodyH * S < scr.Bounds.Top || r.Top >= wa.Bottom) continue;     // no headroom on top
                if (Math.Min(r.Bottom, wa.Bottom) - Math.Max(r.Top, wa.Top) < minEdge) continue;   // edge too short to bother
                // From the taskbar: high enough to be a climb, and its bottom within reach of his mittens.
                if (!Perched && (wa.Bottom - r.Top < hang + 20 * S || r.Bottom < wa.Bottom - hang)) continue;
                foreach (int side in new[] { 1, -1 })
                {
                    double edge = side > 0 ? r.Left : r.Right;
                    double col = edge - side * ClimbGap * S;
                    double land = side > 0 ? r.Left + half + 8 * S : r.Right - half - 8 * S;
                    if (col - half < wa.Left || col + half > wa.Right) continue;   // no room beside it on this monitor
                    if (fg is { } f && Overlaps(f, col, wa.Bottom)) continue;      // the start is under the window you're using
                    var p = new ClimbPlan(h, r, side, !Perched, col, land);
                    if (RouteClear(wins, i, p, wa)) routes.Add(p);
                }
            }
        }
        catch (Exception ex)
        {
            Store.Log($"Climbing: couldn't read window positions ({ex.Message}); not climbing.");
            routes.Clear();
        }
        return routes;
    }

    /// <summary>
    /// Nothing stacked above the window covers its edge, his way up or down beside it, or his spot on its top
    /// (the same test as perching uses, so he isn't knocked straight off when he gets there).
    /// </summary>
    bool RouteClear(List<(IntPtr Hwnd, Native.RECT Rect)> wins, int i, ClimbPlan p, System.Drawing.Rectangle wa)
    {
        double half = BodyHalfW * S, edge = p.Side > 0 ? p.Rect.Left : p.Rect.Right;
        double lo = Math.Min(p.ColumnX - half, edge), hi = Math.Max(p.ColumnX + half, edge);
        double bottom = Math.Min(p.Rect.Bottom, wa.Bottom);
        for (int j = 0; j < i; j++)
        {
            var (h, o) = wins[j];
            if (IgnoredClasses.Contains(Native.ClassName(h))) continue;
            if (o.Left < hi && o.Right > lo && o.Top < bottom && o.Bottom > p.Rect.Top) return false;
            if (Overlaps(o, p.LandX, p.Rect.Top)) return false;
        }
        return true;
    }

    /// <summary>Head for the start of a route: the foot of the edge, or the end of his perch.</summary>
    void GoClimb(ClimbPlan p)
    {
        climb = p;
        double start = p.Up ? p.ColumnX : p.LandX;
        if (Math.Abs(start - x) < 3 * S) { BeginClimb(); return; }
        pendingAct = Act.Climb; pendingX = start;
        WalkTo(start, Store.Config.WalkSpeed);
    }

    /// <summary>Arrived at the start: check the route again (the window may have moved meanwhile), then get going.</summary>
    void BeginClimb()
    {
        var was = climb; climb = null;
        if (was == null) return;
        var p = ClimbRoutes(ScreenAt(x), was.Hwnd).FirstOrDefault(q => q.Side == was.Side && q.Up == was.Up);
        if (p == null || Math.Abs(x - (p.Up ? p.ColumnX : p.LandX)) > 3 * S)
        {
            Store.Log(p == null ? "Climbing: the route went while he walked over; not climbing." : $"Climbing: arrived {Math.Abs(x - (p.Up ? p.ColumnX : p.LandX)) / S:0} DIP off the start; not climbing.");
            nextAct = now + 12 + rng.NextDouble() * 25; return;
        }

        climb = p; facing = p.Side;   // side-on to the edge, facing it
        mode = Mode.Climbing; climbT = 0; climbStageAt = 0; climbDist = 0; nextClimbCheck = now + 0.3;
        double floor = ScreenAt(p.ColumnX).WorkingArea.Bottom;
        if (p.Up)
        {
            climbStage = ClimbStage.Grab;
            climbFromY = y; climbToY = p.Rect.Top + ClimbHang * S;   // until his mittens reach the top
        }
        else
        {
            perchHwnd = IntPtr.Zero;   // stepping off; the climb keeps an eye on the window from here
            climbStage = ClimbStage.StepOver;
            climbFromY = Math.Min(floor, p.Rect.Top + ClimbHang * S);     // hanging from the top edge
            climbToY = Math.Min(floor, p.Rect.Bottom + ClimbHang * S);    // down to the floor, or as low as the edge goes
        }
    }

    /// <summary>Called every frame while he's climbing (Mode.Climbing).</summary>
    void ClimbStep(double dt)
    {
        if (climb is not { } p) { mode = Mode.Idle; return; }
        // Wanted (you clicked him, the chat opened, a bubble or nudge is up): straight down, no fuss.
        if (chat != null || Bubble.Visibility == Visibility.Visible) { Store.Log("Climbing: came down (chat or bubble)."); LetGo(slipped: false); return; }
        if (climbStage != ClimbStage.Release && !ClimbHoldOk(p)) { Store.Log($"Climbing: let go, the window moved or something covered it ({climbStage})."); LetGo(slipped: true); return; }
        if (hovering) return;   // holds on, still, while you point at him
        climbT += dt;
        double st = climbT - climbStageAt;
        switch (climbStage)
        {
            case ClimbStage.Grab:
                if (st >= GrabTime) NextClimbStage(ClimbStage.Climb);
                break;

            case ClimbStage.StepOver:
                // Off the end of the window top, round the corner and down until he's hanging from the edge.
                double so = st / StepOverTime;
                x = Lerp(p.LandX, p.ColumnX, Smooth(so / 0.5));
                y = Lerp(p.Rect.Top, climbFromY, Smooth((so - 0.4) / 0.6));
                if (so >= 1) NextClimbStage(ClimbStage.Climb);
                break;

            case ClimbStage.Climb:
                double step = Store.Config.ClimbSpeed * S * dt;
                if (Math.Abs(climbToY - y) > step) { y += Math.Sign(climbToY - y) * step; climbDist += step / S; break; }
                y = climbToY;
                if (p.Up) NextClimbStage(ClimbStage.Mantle);
                else if (y >= ScreenAt(x).WorkingArea.Bottom - 0.5) NextClimbStage(ClimbStage.Release);
                else LetGo(slipped: false);   // the edge ends above the floor: drop the rest
                break;

            case ClimbStage.Mantle:
                // Pull up level with the top, then step across onto it.
                double m = st / MantleTime;
                y = Lerp(climbToY, p.Rect.Top, Smooth(m / 0.6));
                x = Lerp(p.ColumnX, p.LandX, Smooth((m - 0.45) / 0.55));
                if (m >= 1) ArriveOnTop(p);
                break;

            case ClimbStage.Release:
                if (st >= ReleaseTime) { mode = Mode.Idle; climb = null; nextWander = now + 6; nextAct = now + 12 + rng.NextDouble() * 25; }
                break;
        }
    }

    void NextClimbStage(ClimbStage s) { climbStage = s; climbStageAt = climbT; }

    /// <summary>The window's still there, unmoved, not maximised, and nothing's come over his route. Errors count as no.</summary>
    bool ClimbHoldOk(ClimbPlan p)
    {
        try
        {
            var h = p.Hwnd;
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h) || Native.IsZoomed(h)) return false;
            var r = Native.Bounds(h);
            if (Math.Abs(r.Left - p.Rect.Left) > 2 || Math.Abs(r.Top - p.Rect.Top) > 2
                || Math.Abs(r.Right - p.Rect.Right) > 2 || Math.Abs(r.Bottom - p.Rect.Bottom) > 2) return false;   // moved or resized
            if (now < nextClimbCheck) return true;
            nextClimbCheck = now + 0.3;   // the z-order check enumerates windows, so not every frame
            var wins = Native.TopWindows(ownPid);
            int i = wins.FindIndex(w => w.Hwnd == h);
            return i >= 0 && RouteClear(wins, i, p, ScreenAt(p.ColumnX).WorkingArea);
        }
        catch { return false; }
    }

    /// <summary>Over the top and onto the window: from now on he's perched on it, exactly as if he'd landed there.</summary>
    void ArriveOnTop(ClimbPlan p)
    {
        var r = Native.Bounds(p.Hwnd);
        perchHwnd = p.Hwnd; perchRect = r;
        x = p.LandX + (r.Left - p.Rect.Left); y = r.Top;
        mode = Mode.Idle; climb = null;
        squashUntil = now + 0.2; squashAmp = 0.1;
        nextWander = now + 6; nextAct = now + 12 + rng.NextDouble() * 25;
        if (rng.NextDouble() < 0.3) Say(Pick("Made it.", "Nice view up here.", "I'll be up here. Supervising."), 3);
    }

    /// <summary>
    /// Lets go of the edge and falls to whatever's below (a window top or the taskbar). A slip (the window moved or
    /// went) earns an unimpressed look when he lands; coming down because you wanted him lands without comment.
    /// </summary>
    void LetGo(bool slipped)
    {
        int side = climb?.Side ?? facing;
        climb = null; perchHwnd = IntPtr.Zero;
        mode = Mode.Falling; vy = 0; bounces = 0;
        flingVx = -side * 90 * S;   // a little push off the wall
        landQuiet = true; landSlip = slipped;
    }

    /// <summary>
    /// The climbing pose (side-on, mittens hand over hand on the edge, boots against it, mug hooked on his back) and the
    /// flat look after a slip. Layered over the idle activities' pose; called every frame.
    /// </summary>
    void ClimbPose(ref double eyeCap, ref double? turnOverride, ref double? lookY)
    {
        Shadow.Opacity = 0.5;
        if (mode != Mode.Climbing || climb is not { } p)
        {
            // Just landed after a slip: a flat, half-lidded look at you and a small sigh.
            double ut = now - unimpressedAt;
            if (ut < 2.4 && mode == Mode.Idle && act == Act.None)
            {
                double uk = Smooth(Math.Min(ut / 0.3, (2.4 - ut) / 0.4));
                double sigh = Bump((ut - 0.5) / 1.0);
                eyeCap = Math.Min(eyeCap, 1 - 0.5 * uk);
                turnOverride = 0; lookY = 0.5 * uk;
                Breath.Y += 1.5 * sigh * uk; HoodT.Y += sigh * uk;
            }
            return;
        }

        double st = climbT - climbStageAt;
        double k = climbStage switch
        {
            ClimbStage.Grab => Smooth(st / GrabTime),
            ClimbStage.StepOver => Smooth((st / StepOverTime - 0.2) / 0.5),
            ClimbStage.Mantle => 1 - Smooth((st / MantleTime - 0.4) / 0.6),
            ClimbStage.Release => 1 - Smooth(st / ReleaseTime),
            _ => 1,
        };
        Shadow.Opacity = 0.5 * (1 - k);   // no ground under him on the wall

        // Hand over hand: the holding mitten stays put on the edge while he moves past it; the other lets go, swings
        // a little clear and takes a new hold further on. They swap every stroke.
        double u = climbDist / ClimbStroke; int n = (int)Math.Floor(u); double f = u - n;
        bool leftHolds = n % 2 == 0;
        double hold = p.Up ? Lerp(GripHigh, GripLow, f) : Lerp(GripLow, GripHigh, f);
        double move = p.Up ? Lerp(GripLow, GripHigh, f) : Lerp(GripHigh, GripLow, f);
        double clear = 7 * Bump(f);
        double ly = leftHolds ? hold : move, lx = WallX - 3 - (leftHolds ? 0 : clear);   // the far mitten, a touch behind
        double ry = leftHolds ? move : hold, rx = WallX - (leftHolds ? clear : 0);
        Breath.Y += k * (1.2 * Bump(f) - Breath.Y);   // no breathing bob; a small heave with each pull
        LeftGloveT.X += k * (lx - 29 - LeftGloveT.X); LeftGloveT.Y += k * (ly - 106.5 - Breath.Y - LeftGloveT.Y);
        RightGloveT.X += k * (rx - 99 - RightGloveT.X); RightGloveT.Y += k * (ry - 102.5 - Breath.Y - RightGloveT.Y);

        // Boots pressed to the edge, stepping with the free mitten's side.
        double lift = 5 * Bump(f);
        LeftFootT.X += k * (36 - LeftFootT.X); LeftFootT.Y += k * (-2 - (leftHolds ? 0 : lift) - LeftFootT.Y);
        RightFootT.X += k * (16 - RightFootT.X); RightFootT.Y += k * (-2 - (leftHolds ? lift : 0) - RightFootT.Y);

        // Mug hooked on his back (the side away from the wall), kept upright so nothing spills.
        MugT.X += k * (-58 - MugT.X); MugT.Y += k * (18 - MugT.Y);
        MugR.Angle *= 1 - k;

        // Side-on: a bit narrower, leaning into the wall, face turned to it and looking where he's going.
        Squash.ScaleX *= 1 - 0.1 * k;
        Lean.Angle += k * (3 * p.Side - Lean.Angle);
        turnOverride = 28 * k;
        lookY = (p.Up ? -2.5 : 3) * k;
    }
}
