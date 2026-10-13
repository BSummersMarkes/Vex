using System;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// Lunch break. When you head off for lunch (the lunch nudge's "Going" button, or right-click → Lunch break),
/// Vex walks to the nearer corner, sits down, opens his lunch box and eats a sandwich bite by bite, then shuts
/// the box and dozes for the rest of the break. Lengths: LunchMinutes / LunchEatMinutes in config.json.
/// He doesn't dodge or wander meanwhile. Dragging him, clicking him, or a meeting/Claude nudge ends it early.
/// </summary>
public partial class VexWindow
{
    double eatEnd, lastBite;
    int bites;

    bool AtLunch => act == Act.Lunch || pendingAct == Act.Lunch;

    public void StartLunch()
    {
        if (mode == Mode.Sleeping) Wake(quiet: true);
        act = Act.None; peeking = false;
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

    void BeginLunch(Forms.Screen scr)
    {
        if (!Perched)
        {
            bool edgeLeft = x - scr.WorkingArea.Left < scr.WorkingArea.Right - x;
            facing = edgeLeft ? 1 : -1;
            sitLean = edgeLeft ? -5 : 5;
        }
        else sitLean = 0;
        var c = Store.Config;
        Begin(Act.Lunch, Math.Max(2, c.LunchMinutes) * 60);
        eatEnd = now + 2 + Math.Clamp(c.LunchEatMinutes, 0.5, c.LunchMinutes - 0.5) * 60;
        bites = 0; lastBite = now;
    }

    /// <summary>Clicked during lunch: that's the end of that.</summary>
    void InterruptLunch()
    {
        if (act != Act.Lunch) return;
        actEnd = Math.Min(actEnd, now + 0.6);
        Say(Pick("I was on my lunch break, you know.", "Fine. Lunch is over, apparently.", "Mmf. What."), 4);
    }

    void LunchPose(ref double eyeCap, ref double? turnOverride, ref double? lookY)
    {
        double k = Blend(actStart, actEnd, 0.8);
        Breath.Y += 9 * k;                                  // sat down, like Sit
        LeftFootT.X -= 4 * k; RightFootT.X += 4 * k;
        Lean.Angle = sitLean * k;
        LunchBox.Opacity = k;

        double t = now - actStart;
        if (now < eatEnd)
        {
            // Lid opens, sandwich comes out, then bite / chew cycles every 6 s. The sandwich gets smaller.
            LidR.Angle = -80 * Smooth(t / 1.2);
            double sk = Smooth(Math.Min((t - 1.4) / 0.6, (eatEnd - now) / 0.6));
            Sandwich.Opacity = sk > 0.02 ? 1 : 0;
            double totalBites = Math.Max(1, (eatEnd - actStart - 2) / 6);
            SandwichS.ScaleX = SandwichS.ScaleY = 1 - 0.85 * Math.Min(1, bites / totalBites);

            double ct = t < 2 ? 99 : (t - 2) % 6.0;
            double raise = ct < 0.7 ? Smooth(ct / 0.7) : ct < 1.6 ? 1 : ct < 2.3 ? 1 - Smooth((ct - 1.6) / 0.7) : 0;
            LeftGloveT.X += (14 + 15 * raise) * sk;
            LeftGloveT.Y += (-6 - 26 * raise) * sk;
            if (ct >= 1.0 && ct < 1.2 && now - lastBite > 3) { bites++; lastBite = now; }
            if (ct >= 1.0 && ct < 4) Breath.Y += Math.Sin(now * 14) * 0.5;      // chewing
            eyeCap = Math.Min(eyeCap, raise > 0.5 ? 0.35 : 0.85);
            turnOverride = -5; lookY = 2.5;
        }
        else
        {
            // Box shut, eyes shut. Zzz till the end of lunch.
            Sandwich.Opacity = 0;
            LidR.Angle = -80 * Math.Max(0, 1 - (now - eatEnd) / 0.8);
            double nap = Smooth((now - eatEnd) / 1.5);
            eyeCap = Math.Min(eyeCap, 1 - 0.88 * nap);
            Breath.Y += 1.5 * nap + Math.Sin(now * 1.2) * 0.8 * nap;            // head droops, slow breathing
            Zzz.Visibility = nap > 0.6 && k > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            turnOverride = 0; lookY = 2 * nap;
        }
    }
}
