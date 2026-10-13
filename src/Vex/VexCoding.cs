using System;
using System.Linq;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// Coding at the laptop. While his workshop is building (and, with CodingForClaude, while a Claude Code session is
/// working) Vex walks to a bottom corner of his screen (CodingCorner), puts his mug down beside him, sits, and types
/// on a little laptop whose screen lights his face. When it's over he shuts the lid, stands, gives a quiet nod
/// (it worked) or a dry shrug (it didn't), and strolls back to where he was. Setup → Behaviour turns it off.
/// He still dodges your window, knocks for nudges from his seat, and can be clicked, right-clicked or dragged;
/// dropped somewhere, he carries on typing there instead of walking back to the corner.
/// </summary>
public partial class VexWindow
{
    const double LidShut = 0.4, StandUp = 1.3, ReactLength = 1.4;   // seconds into packing up

    double codeWrap = -1;       // when he started packing up; -1 while still typing
    int codeReact;              // +1 nod, -1 shrug, 0 just stand up
    double? codeHomeX;          // where to stroll back to afterwards
    bool codeHere;              // you moved him mid-job: work wherever he is now, not in the corner
    bool codeShop, codeClaude;  // what he's been working for this time, to know how to react
    double codeDebugUntil;

    bool Coding => act == Act.Code || pendingAct == Act.Code;

    static bool ClaudeWorking()
    {
        var c = Store.Config;
        if (!c.CodingForClaude) return false;
        var t = DateTime.Now;
        return Services.Hub.Claude.Sessions.Values.Any(s => s.State == Services.ClaudeState.Working
            && (t - s.Since).TotalSeconds >= c.CodingClaudeAfterSeconds && (t - s.Since).TotalMinutes < c.CodingClaudeMaxMinutes);
    }

    bool CodingWanted() => Store.Config.CodingAtLaptop && (Services.Workshop.Job != null || ClaudeWorking() || now < codeDebugUntil);

    /// <summary>Called every frame from UpdateActs: heads off to the laptop, packs up, and walks home again.</summary>
    void UpdateCoding(Forms.Screen scr)
    {
        bool want = CodingWanted();
        if (mode is Mode.Dragging or Mode.Hopping) { codeHomeX = null; codeHere = want; }   // you put him somewhere: that's his spot now
        if (want) { codeShop |= Services.Workshop.Job != null; codeClaude |= ClaudeWorking(); }

        if (!want)
        {
            if (act == Act.Code) { EndCoding(); return; }
            if (pendingAct == Act.Code) { pendingAct = Act.None; if (mode == Mode.Walking) mode = Mode.Idle; }   // finished before he got there
            codeHere = codeShop = codeClaude = false;
            GoHome();
            return;
        }
        if (act == Act.Code && peeking) actEnd = Math.Min(actEnd, now + 0.35);   // ducked down out of the way: no room to type
        if (Coding || AtLunch) return;
        if (act != Act.None) { actEnd = Math.Min(actEnd, now + 0.6); return; }   // put the book down first
        // Same manners as wandering: not mid-conversation, not with a nudge waiting, not while you're pointing at him.
        if (mode != Mode.Idle || peeking || hovering || hidden || autoHidden || chat != null || currentNudge != null) return;
        GoCode(scr);
    }

    /// <summary>Walk to the corner (unless it's covered by your window, he's perched, or you've just moved him).</summary>
    void GoCode(Forms.Screen scr)
    {
        if (Perched || codeHere) { BeginCode(scr, againstEdge: false); return; }
        var wa = scr.WorkingArea;
        double half = BodyHalfW * S;
        bool left = Store.Config.CodingCorner.Trim().Equals("left", StringComparison.OrdinalIgnoreCase);
        double tx = left ? wa.Left + half + 2 * S : wa.Right - half - 2 * S;
        if (ForegroundRect() is { } r && Overlaps(r, tx, wa.Bottom)) { BeginCode(scr, againstEdge: false); return; }
        codeHomeX ??= x;
        if (Math.Abs(tx - x) < 3 * S) { BeginCode(scr, againstEdge: true); return; }
        pendingAct = Act.Code; pendingX = tx;
        WalkTo(tx, Store.Config.WalkSpeed * 1.3);
    }

    void BeginCode(Forms.Screen scr, bool againstEdge)
    {
        if (againstEdge)
        {
            bool edgeLeft = x - scr.WorkingArea.Left < scr.WorkingArea.Right - x;
            facing = edgeLeft ? 1 : -1;          // back to the edge, looking into the screen
            sitLean = edgeLeft ? -5 : 5;
        }
        else sitLean = 0;
        Begin(Act.Code, double.PositiveInfinity);   // until the job's done
        codeWrap = -1; codeReact = 0;
    }

    /// <summary>The job's over: shut the lid, stand up, and react to how it went.</summary>
    void EndCoding()
    {
        if (act != Act.Code || codeWrap >= 0) return;
        codeWrap = now;
        var t = DateTime.Now;
        codeReact = codeShop && Services.Workshop.Job == null && Services.Workshop.LastOk is { } ok ? (ok ? 1 : -1)
                  : codeClaude && Services.Hub.Claude.Sessions.Values.Any(s => s.State == Services.ClaudeState.Finished && (t - s.Since).TotalSeconds < 15) ? 1
                  : now < codeDebugUntil + 1 ? 1 : 0;
        codeShop = codeClaude = false;
        actEnd = Math.Min(actEnd, now + StandUp + (codeReact != 0 ? ReactLength : 0));
    }

    /// <summary>
    /// If he's packing up (or about to, because the job just ended), when he'll be done; a knock waits till then so the
    /// workshop's result doesn't land mid-nod. 0 if he's not.
    /// </summary>
    double CodingWrapEnd()
    {
        if (act == Act.Code && codeWrap < 0 && !CodingWanted()) EndCoding();
        return act == Act.Code && codeWrap >= 0 ? actEnd : 0;
    }

    /// <summary>Back to where he was before, once nothing's waiting on him and the spot's still clear.</summary>
    void GoHome()
    {
        if (codeHomeX is not { } hx || act != Act.None || pendingAct != Act.None || mode != Mode.Idle) return;
        if (peeking || hovering || chat != null || currentNudge != null) return;
        codeHomeX = null;
        if (Perched || Math.Abs(hx - x) < 3 * S || !Walkable(x, hx)) return;
        if (ForegroundRect() is { } r && Overlaps(r, hx, ScreenAt(hx).WorkingArea.Bottom)) return;   // his spot's taken; stay put
        WalkTo(hx, Store.Config.WalkSpeed);
    }

    void CodePose(ref double eyeCap, ref double? turnOverride, ref double? lookY)
    {
        // Sit down, then the laptop comes out (mug down beside him, mittens to the keys), then the lid opens.
        // Packing up runs the same in reverse: lid shuts, laptop away, mug back in hand, stand up.
        double wrap = codeWrap >= 0 ? codeWrap : double.PositiveInfinity;
        double sitK = Blend(actStart, Math.Min(actEnd, wrap + StandUp), 0.8);
        double lapK = Blend(actStart + 0.5, Math.Min(actEnd, wrap + 0.9), 0.5);
        double lidK = Blend(actStart + 1.0, Math.Min(actEnd, wrap + LidShut), LidShut);

        Breath.Y += 9 * sitK;
        LeftFootT.X -= 4 * sitK; RightFootT.X += 4 * sitK;
        Lean.Angle = sitLean * sitK;

        Laptop.Opacity = Math.Min(1, lapK * 2);
        LidS.ScaleY = 0.12 + 0.88 * lidK;

        // Mug on the floor by his side: lowered to the ground whatever his breathing does, and a touch more when he
        // leans back against the edge (the lean lifts that side).
        MugT.X += 13 * lapK;
        MugT.Y += lapK * (42 + Math.Abs(sitLean) * 0.7 * sitK - Breath.Y - MugT.Y);

        // Typing: each mitten taps in its own uneven rhythm. Every 9 s he stops for a moment to think.
        double phase = (now - actStart) % 9;
        double pause = phase < 7.2 ? 0 : Smooth(Math.Min((phase - 7.2) / 0.3, (9 - phase) / 0.3));
        double typing = lidK * (1 - pause);
        double tapL = Math.Max(0, Math.Sin(now * 19)) * (0.5 + 0.5 * Math.Sin(now * 3.1));
        double tapR = Math.Max(0, Math.Sin(now * 17 + 1.7)) * (0.5 + 0.5 * Math.Sin(now * 2.6 + 1));
        LeftGloveT.X += 13 * lapK + Math.Sin(now * 1.3) * 0.8 * typing;
        LeftGloveT.Y += 15.5 * lapK - 1.8 * tapL * typing;
        RightGloveT.X += -21 * lapK - Math.Sin(now * 1.1 + 2) * 0.8 * typing;
        RightGloveT.Y += 19.5 * lapK - 1.8 * tapR * typing;
        LaptopGlow.Opacity = lidK * (0.7 + 0.12 * Math.Sin(now * 1.9) + 0.1 * Math.Max(tapL, tapR) * typing);

        if (lapK > 0.3)
        {
            eyeCap = Math.Min(eyeCap, 0.8 + 0.2 * pause);                  // focused, eyes on the screen...
            lookY = 3.5 - 3 * pause;                                        // ...looking up while he thinks
            turnOverride = pause * Math.Sin(now * 0.8) * 12;
        }

        if (codeWrap < 0 || codeReact == 0) return;
        double r = Math.Clamp((now - codeWrap - StandUp) / ReactLength, 0, 1);
        if (r <= 0) return;
        turnOverride = 0;
        if (codeReact > 0)
        {
            // Quiet nod: one slow dip of the head, eyes softening.
            double dip = Math.Sin(Math.PI * Math.Clamp((r - 0.15) / 0.55, 0, 1));
            HoodT.Y += 2.8 * dip;
            lookY = 2 * dip;
            eyeCap = Math.Min(eyeCap, 1 - 0.45 * dip);
        }
        else
        {
            // Dry shrug: mittens (and mug) up and out, shoulders up, head sinks a touch, flat half-lidded eyes.
            double s = Math.Sin(Math.PI * r);
            LeftGloveT.X -= 4 * s; LeftGloveT.Y -= 8 * s;
            RightGloveT.X += 4 * s; RightGloveT.Y -= 8 * s; MugT.X += 4 * s; MugT.Y -= 8 * s;
            Breath.Y -= 1.5 * s; HoodT.Y += 1.2 * s;
            Lean.Angle += 3 * s;
            lookY = 0;
            eyeCap = Math.Min(eyeCap, 1 - 0.45 * s);
        }
    }
}
