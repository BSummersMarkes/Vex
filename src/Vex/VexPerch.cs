using System;
using System.Linq;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// Falling and perching. Dropped from a height, he lands on the first window top edge he passes
/// (if nothing's covering that spot), then stands on that window: moves with it, strolls along it,
/// and falls off if it closes, minimises, maximises, shrinks out from under him, or something covers him
/// (landing with an unimpressed look rather than a remark). He can also get up there by climbing (VexClimb.cs).
/// </summary>
public partial class VexWindow
{
    IntPtr perchHwnd;
    Native.RECT perchRect;
    int bounces;
    double squashAmp = 0.08;

    bool Perched => perchHwnd != IntPtr.Zero;

    /// <summary>Testing aid (debug endpoint): drop him from a screen point, "x,y" in physical pixels.</summary>
    internal string DebugDrop(string at)
    {
        var p = at.Split(',');
        if (p.Length != 2 || !double.TryParse(p[0], out var px) || !double.TryParse(p[1], out var py)) return "use x,y";
        act = Act.None; perchHwnd = IntPtr.Zero; peeking = false; climb = null; landQuiet = landSlip = false;
        x = px; y = py; vy = 0; flingVx = 0; bounces = 0; mode = Mode.Falling;
        return "dropped";
    }

    internal string DebugLedges(string atX)
    {
        double px = double.TryParse(atX, out var v) ? v : x;
        var wins = Native.TopWindows(ownPid);
        return string.Join("\n", wins.Take(15).Select(w => $"{w.Rect.Left},{w.Rect.Top},{w.Rect.Right},{w.Rect.Bottom} zoomed={Native.IsZoomed(w.Hwnd)} cls={Native.ClassName(w.Hwnd)} \"{Native.WindowText(w.Hwnd)}\""));
    }

    internal string DebugPerch() => Perched ? $"perched on \"{Native.WindowText(perchHwnd)}\" top={perchRect.Top} feet={y:0}" : $"on the floor, feet={y:0}, mode={mode}";

    void FallStep(Forms.Screen scr, double dt)
    {
        double prevY = y;
        vy += 2600 * S * dt;
        y += vy * dt;
        x += flingVx * dt; flingVx *= Math.Pow(0.2, dt);
        var wa = scr.WorkingArea;
        x = Math.Clamp(x, wa.Left + BodyHalfW * S, wa.Right - BodyHalfW * S);

        // A window top below the taskbar's isn't somewhere to land: the taskbar catches him first.
        if (vy > 0 && FindLedge(prevY, y) is { } ledge && ledge.Rect.Top <= wa.Bottom) { Land(ledge.Rect.Top, ledge.Hwnd, ledge.Rect); return; }
        if (y >= wa.Bottom) Land(wa.Bottom, IntPtr.Zero, default);
    }

    void Land(double top, IntPtr on, Native.RECT rect)
    {
        y = top;
        if (vy > 900 * S && bounces < 2)
        {
            // A little bounce first.
            vy = -vy * 0.3; bounces++; flingVx *= 0.5;
            squashUntil = now + 0.16; squashAmp = 0.16;
            return;
        }
        vy = 0; bounces = 0; mode = Mode.Idle; peeking = false;
        perchHwnd = on; perchRect = rect;
        squashUntil = now + 0.2; squashAmp = 0.12;
        nextWander = now + 4;
        // He let go himself (VexClimb.cs) or his window went from under him: no "Rude." for being dropped. A slip
        // gets a flat, unimpressed look instead, and now and then a word.
        bool quiet = landQuiet, slip = landSlip;
        landQuiet = landSlip = false;
        if (slip) unimpressedAt = now;
        if (quiet)
        {
            if (slip && rng.NextDouble() < 0.3) Say(Pick("...noted.", "I was using that.", "Fine. I'll stand here."), 3);
        }
        else if (rng.NextDouble() < 0.45)
            Say(on != IntPtr.Zero
                ? Pick("Nice view up here.", "I live here now.", "Don't close this one.", "...fine. I'll stand on this.")
                : Pick("Rude.", "I had a mug, you know.", "...thanks.", "I'll just stand here then.", "Wheee. (That's sarcasm.)"), 3);
    }

    /// <summary>The highest window top edge crossed between two heights, under his feet and not covered by anything.</summary>
    (IntPtr Hwnd, Native.RECT Rect)? FindLedge(double fromY, double toY)
    {
        var wins = Native.TopWindows(ownPid);
        (IntPtr, Native.RECT)? best = null;
        double margin = 24 * S, room = BodyH * S;
        for (int i = 0; i < wins.Count; i++)
        {
            var (h, r) = wins[i];
            if (r.Top < fromY - 1 || r.Top > toY) continue;                 // not crossed this frame
            if (x < r.Left + margin || x > r.Right - margin) continue;      // not under his feet
            if (Native.IsZoomed(h) || IgnoredClasses.Contains(Native.ClassName(h))) continue;
            var scr = Forms.Screen.FromPoint(new System.Drawing.Point((int)x, r.Top));
            if (r.Top - room < scr.Bounds.Top) continue;                    // no headroom
            bool covered = false;                                           // a window above it in z-order over that spot?
            for (int j = 0; j < i && !covered; j++)
            {
                var o = wins[j].Rect;
                covered = x >= o.Left && x <= o.Right && ((r.Top + 2 >= o.Top && r.Top + 2 <= o.Bottom) || (r.Top - 30 * S >= o.Top && r.Top - 30 * S <= o.Bottom));
            }
            if (covered) continue;
            if (best == null || r.Top < best.Value.Item2.Top) best = (h, r);
        }
        return best;
    }

    /// <summary>Where his feet go this frame if he's perched (following the window if it moved), else null.</summary>
    double? PerchFloor()
    {
        if (!Perched) return null;
        var h = perchHwnd;
        if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h) || Native.IsZoomed(h))
        {
            FallOff(); return null;
        }
        var r = Native.Bounds(h);
        int dx = r.Left - perchRect.Left, dy = r.Top - perchRect.Top;
        if (dx != 0 || dy != 0)
        {
            // Ride along with the window.
            x += dx; y += dy;
            if (mode == Mode.Walking) targetX += dx;
        }
        perchRect = r;
        if (x < r.Left + 8 * S || x > r.Right - 8 * S) { FallOff(); return null; }
        // Window dragged down behind the taskbar, or the taskbar grew over it: off he gets, onto the taskbar.
        if (r.Top > ScreenAt(x).WorkingArea.Bottom) { FallOff(); return null; }
        return r.Top;
    }

    void FallOff()
    {
        perchHwnd = IntPtr.Zero;
        if (mode is Mode.Dragging or Mode.Hopping) return;
        act = Act.None; pendingAct = Act.None;
        mode = Mode.Falling; vy = -200 * S; flingVx = 0; // a little hop off
        landQuiet = landSlip = true;                     // not his doing: lands with an unimpressed look (VexClimb.cs)
    }

    /// <summary>
    /// Perched version of staying out of the way: if any window stacked above his perch now overlaps
    /// him (you brought something to the front over him), step off.
    /// </summary>
    void CheckPerch()
    {
        foreach (var (h, r) in Native.TopWindows(ownPid))
        {
            if (h == perchHwnd) return;                   // reached his window: nothing above covers him
            if (IgnoredClasses.Contains(Native.ClassName(h))) continue;
            if (Overlaps(r, x, y)) { FallOff(); return; }
        }
    }

    /// <summary>Where he can stroll: the top of his window if perched, otherwise the taskbar.</summary>
    (double Lo, double Hi) WalkRange(Forms.Screen scr)
    {
        double half = BodyHalfW * S;
        if (Perched) return (perchRect.Left + half + 6 * S, perchRect.Right - half - 6 * S);
        return (scr.WorkingArea.Left + half + 10 * S, scr.WorkingArea.Right - half - 10 * S);
    }

    /// <summary>Flailing while falling, dangling while carried. Layered over everything else.</summary>
    void AirPose()
    {
        if (mode == Mode.Falling)
        {
            double f = now * 20;
            LeftGloveT.X = -8 + Math.Sin(f) * 4; LeftGloveT.Y = -32 + Math.Cos(f) * 6;
            RightGloveT.X = MugT.X = 6 + Math.Sin(f + 2) * 3;
            RightGloveT.Y = MugT.Y = -26 + Math.Cos(f + 1) * 5;
            MugR.Angle = Math.Sin(f) * 15;
            LeftFootT.X = -3; RightFootT.X = 3;
            LeftFootT.Y = 4 + Math.Sin(f * 0.8) * 3; RightFootT.Y = 4 - Math.Sin(f * 0.8) * 3;
            Lean.Angle = Math.Clamp(flingVx / (60 * S), -25, 25) + Math.Sin(now * 9) * 8;
            Blink.ScaleY = 1.25; // wide-eyed
        }
        else if (mode == Mode.Dragging)
        {
            LeftGloveT.Y = 4; RightGloveT.Y = MugT.Y = 3;
            LeftFootT.Y = 3 + Math.Sin(now * 5) * 1.5; RightFootT.Y = 3 + Math.Sin(now * 5 + 2) * 1.5;
            Lean.Angle = Math.Clamp(-flingVx / (80 * S), -20, 20) + Math.Sin(now * 4) * 3;
        }
    }
}
