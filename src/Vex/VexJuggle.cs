using System;

namespace Vex;

/// <summary>
/// Juggling, one of his idle activities. He puts his mug down beside him (it only moves: the level carries on from
/// RefilledAt as always), juggles three balls in a cascade for about JuggleSeconds with his eyes on the highest ball,
/// catches them, pockets them and picks the mug back up. Now and then (JuggleDropChance) one gets away: it bounces off
/// along the floor, he gives it a long flat look and a sigh, picks it up, and that's the end of the show.
/// Cascade: one throw per beat, alternating hands; each ball is thrown every third beat, spends two beats in the air
/// and one in a mitten being carried from the outside (where it's caught) to the inside (where it's thrown).
/// Positions are in the rig's 120 x 150 units. The balls sit outside the breathing group (so a dropped one stays on
/// the floor), so anything held or thrown adds Breath.Y.
/// </summary>
public partial class VexWindow
{
    const double JuggleBeat = 0.27;    // seconds per throw
    const double JuggleIn = 1.2;       // mug down and mittens up before the first throw
    const double HandY = 103;          // height of the mittens while juggling (centre)
    const double BallLift = 8;         // a held ball sits this far above the mitten's centre
    static readonly double[] HandIn = { 50, 70 }, HandOut = { 39, 81 };   // throw / catch x, left and right mitten
    static readonly (double X, double Y) LeftRest = (29, 106.5), RightRest = (99, 102.5);
    static readonly (double X, double Y)[] HoldAt = { (44, 103), (76, 103) }, PocketAt = { (52, 116), (68, 116) };

    int juggleThrows;      // throws this time, at beats 0 … juggleThrows-1
    int juggleDrop = -1;   // the beat whose throw goes wrong, or -1
    double juggleDone;     // seconds into the act when all three balls are back in his mittens

    void StartJuggle()
    {
        var c = Store.Config;
        int beats = Math.Max(6, (int)Math.Round(Math.Max(1, c.JuggleSeconds) / JuggleBeat));
        juggleDrop = -1;
        if (rng.NextDouble() < c.JuggleDropChance)
        {
            // Odd beats are the right mitten's throws, so the stray ball always goes left, away from the mug on the floor.
            // A few clean throws first, so it reads as juggling that went wrong rather than never starting.
            juggleDrop = 5 + 2 * rng.Next(Math.Max(1, (beats - 6) / 2));
            beats = juggleDrop + 1;   // nobody carries on after that
        }
        juggleThrows = beats;
        juggleDone = juggleDrop < 0
            ? JuggleIn + (beats + 1) * JuggleBeat + 0.7                // last catch, then a moment's quiet pride
            : JuggleIn + (juggleDrop + 3.2) * JuggleBeat + 2.9;        // ball stops rolling, a long look, picks it up
        Begin(Act.Juggle, juggleDone + 1.6);                           // pocket the balls, pick up the mug
    }

    static double Lerp(double a, double b, double u) => a + (b - a) * u;
    static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double u) => (Lerp(a.X, b.X, u), Lerp(a.Y, b.Y, u));
    static double Bump(double u) => Math.Sin(Math.PI * Math.Clamp(u, 0, 1));

    /// <summary>Where a mitten is (centre, in the breathing group) a given number of beats into the cascade.</summary>
    static (double X, double Y) HandAt(int h, double beats)
    {
        // Each mitten throws on every other beat (left on even, right on odd) and catches the beat before.
        double u = ((beats - (h - 1)) % 2 + 2) % 2;
        if (u < 1) return (Lerp(HandOut[h], HandIn[h], u), HandY + 4 * Math.Sin(Math.PI * u));   // carrying a ball in, a little scoop
        return (Lerp(HandIn[h], HandOut[h], u - 1), HandY - 2 * Math.Sin(Math.PI * (u - 1)));    // empty, back out to catch
    }

    void JugglePose(ref double eyeCap, ref double? turnOverride, ref double? lookY)
    {
        double t = now - actStart, k = Smooth((actEnd - now) / 0.35);   // k eases it all back if he's cut short
        double tb = (t - JuggleIn) / JuggleBeat;                         // beats since the first throw
        double done = juggleDone;
        bool dropped = juggleDrop >= 0;
        double lastCatch = dropped ? juggleThrows : juggleThrows + 1;   // in beats
        double caughtAt = JuggleIn + lastCatch * JuggleBeat;
        double restsAt = dropped ? JuggleIn + (juggleDrop + 3.2) * JuggleBeat : double.MaxValue;   // stray ball stops rolling

        // Bending down: to put the mug down and pick it up again, and to fetch the stray ball. Plus a sigh at it.
        double m, crouch;
        if (t < 0.6) { m = Smooth(t / 0.6); crouch = m; }
        else if (t < done + 1.0) { m = 1; crouch = t < JuggleIn ? 1 - Smooth((t - 0.6) / 0.6) : t > done + 0.6 ? Smooth((t - done - 0.6) / 0.4) : 0; }
        else { m = 1 - Smooth((t - done - 1.0) / 0.6); crouch = m; }
        double reach = 0, sigh = dropped ? Bump((t - restsAt - 0.5) / 0.8) : 0;
        if (dropped && t > restsAt + 1.5 && t < done)
        {
            double r = (t - restsAt - 1.5) / 1.4;
            reach = r < 0.45 ? Smooth(r / 0.45) : r < 0.6 ? 1 : 1 - Smooth((r - 0.6) / 0.4);
        }
        bool grabbed = dropped && t > restsAt + 1.5 + 0.45 * 1.4;
        Breath.Y += k * (7 * crouch + 9 * reach + 1.5 * sigh);
        HoodT.Y += k * sigh;

        // Mittens. The right one goes down with the mug and comes back for it; the left one fetches the stray ball.
        var withMug = (X: RightRest.X + 13 * m, Y: RightRest.Y + m * (42 - Breath.Y));
        (double X, double Y) L, R;
        if (t < JuggleIn)
        {
            R = t < 0.6 ? withMug : Lerp(withMug, HandAt(1, 0), Smooth((t - 0.6) / 0.6));
            L = Lerp(LeftRest, HandAt(0, 0), Smooth((t - 0.4) / 0.8));
        }
        else if (t < done)
        {
            double settle = Smooth((t - caughtAt) / 0.3);
            L = Lerp(HandAt(0, Math.Min(tb, lastCatch)), HoldAt[0], settle);
            R = Lerp(HandAt(1, Math.Min(tb, lastCatch)), HoldAt[1], settle);
            if (reach > 0) L = Lerp(L, (13, 141 - Breath.Y), reach);
        }
        else if (t < done + 0.6)
        {
            double p = Smooth((t - done) / 0.6);
            L = Lerp(HoldAt[0], PocketAt[0], p); R = Lerp(HoldAt[1], PocketAt[1], p);
        }
        else
        {
            L = Lerp(PocketAt[0], LeftRest, Smooth((t - done - 0.6) / 0.5));
            R = t < done + 1.0 ? Lerp(PocketAt[1], withMug, Smooth((t - done - 0.6) / 0.4)) : withMug;
        }
        LeftGloveT.X += k * (L.X - LeftRest.X); LeftGloveT.Y += k * (L.Y - LeftRest.Y);
        RightGloveT.X += k * (R.X - RightRest.X); RightGloveT.Y += k * (R.Y - RightRest.Y);
        // Mug on the floor beside him, as at lunch.
        MugT.X += 13 * m * k;
        MugT.Y += m * k * (42 - Breath.Y - MugT.Y);

        // Balls: in a mitten (following it, side by side if it has two), in the air, or the stray one on the floor.
        var hands = new[] { (X: LeftRest.X + LeftGloveT.X, Y: LeftRest.Y + LeftGloveT.Y + Breath.Y),
                            (X: RightRest.X + RightGloveT.X, Y: RightRest.Y + RightGloveT.Y + Breath.Y) };
        var balls = new[] { Ball0, Ball1, Ball2 };
        var held = new int[2];
        (double X, double Y)? watch = null, stray = null;   // highest ball in the air; the one getting away (that wins)
        for (int i = 0; i < 3; i++)
        {
            int n = i + 3 * (int)Math.Floor((tb - i) / 3);   // this ball's latest throw
            while (n >= juggleThrows) n -= 3;
            int hand = -1; (double X, double Y) at = default;
            if (n < i) hand = i % 2;                            // not thrown yet: still in the mitten it starts in
            else
            {
                double p = tb - n; int from = n % 2;
                if (n == juggleDrop)
                {
                    if (grabbed) hand = 0;
                    else if (p < 2.4)
                    {
                        // Off the end of his mitten, past the other one and down.
                        double u = p / 2.4;
                        at = (Lerp(HandIn[1], 17, u), Lerp(HandY - BallLift + Breath.Y, 145, u) - 120 * u * (1 - u));
                        stray = at;
                    }
                    else if (p < 3.2) { double q = (p - 2.4) / 0.8; at = (17 - 4 * q, 145 - 6 * Bump(q)); stray = at; }   // one bounce, a little roll
                    else at = (13, 145);
                }
                else if (p < 2)
                {
                    double u = p / 2;
                    at = (Lerp(HandIn[from], HandOut[1 - from], u), HandY - BallLift + Breath.Y - 172 * u * (1 - u));   // peaks about eye height
                    if (watch == null || at.Y < watch.Value.Y) watch = at;
                }
                else hand = 1 - from;
            }
            if (hand >= 0)
            {
                int j = held[hand]++;
                at = (hands[hand].X + j * (hand == 0 ? -5.5 : 5.5), hands[hand].Y - BallLift + j * 1.5);
            }
            balls[i].X = at.X; balls[i].Y = at.Y;
        }
        Balls.Opacity = k * Smooth((t - 0.8) / 0.4) * (1 - Smooth((t - done - 0.3) / 0.3));   // out of the pocket, back in

        // Eyes: on the mug going down, then the highest ball, then (clean finish) a smug half-blink, or (dropped) a
        // flat, unimpressed stare at the floor while he decides whether it's worth it.
        if (t < JuggleIn) { turnOverride = 0; lookY = 1 + 2.5 * crouch; }
        else if (dropped && t >= restsAt && t < done) { turnOverride = -24; lookY = 3.5; eyeCap = Math.Min(eyeCap, reach > 0 ? 0.75 : 0.5); }
        else if (t < (dropped ? restsAt : caughtAt) && (stray ?? watch) is { } w)
        {
            turnOverride = Math.Clamp((w.X - 60) * 0.8, -22, 22);
            lookY = Math.Clamp((w.Y - 51) / 12, -3, 3.5);
        }
        else if (t < done) { turnOverride = 0; lookY = 1; eyeCap = Math.Min(eyeCap, 1 - 0.45 * Bump((t - caughtAt) / 0.7)); }
        else { turnOverride = 0; lookY = 2.5; }
    }
}
