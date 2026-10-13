using System;
using System.IO;
using System.Media;

namespace Vex;

/// <summary>
/// Getting attention: Vex steps up to the glass and knocks (mitten looms toward you, three raps with a
/// ripple each, a soft knock sound). Nudge bubbles then stay until answered; he knocks again every
/// 90 seconds, up to three more times, while you're at the desk.
/// </summary>
public partial class VexWindow
{
    const double KnockLength = 1.35;
    double knockStart = -99, rippleStart = -99, lastKnock, knockAt;
    int lastTap = -1, reknocks;
    Services.Nudge? currentNudge;
    static byte[]? knockWav;

    void Knock()
    {
        // Packing the laptop away (e.g. the workshop's result just came in): stand up and react first, then knock.
        if (CodingWrapEnd() is var until && until > now) { knockAt = until; lastKnock = now; return; }
        knockAt = 0;
        knockStart = now; lastTap = -1; lastKnock = now;
        if (Store.Config.KnockSound && Services.Hub.InCall == null) PlayKnock();
    }

    bool Knocking => now - knockStart < KnockLength;

    /// <summary>Called every frame while a nudge is up: re-knock if unanswered, drop it if it's gone stale.</summary>
    void UpdateNudge()
    {
        if (currentNudge == null) return;
        if (Bubble.Visibility != System.Windows.Visibility.Visible) { currentNudge = null; return; }
        if (currentNudge.StillValid is { } valid && !valid()) { bubbleTimeout = null; HideBubble(); return; }
        if (knockAt > 0 && now >= knockAt) { Knock(); return; }   // a knock held back while he put the laptop away
        if (reknocks < 3 && now - lastKnock > 90 && !Services.Hub.Presence.Away && mode != Mode.Dragging)
        {
            reknocks++;
            Knock();
        }
    }

    /// <summary>The knock pose, layered over everything else. Returns true while knocking.</summary>
    bool KnockPose()
    {
        double t = now - knockStart;
        if (t >= KnockLength)
        {
            LeftGloveS.ScaleX = LeftGloveS.ScaleY = 1;
            return false;
        }
        double lift = Math.Clamp(Math.Min(t / 0.25, (KnockLength - t) / 0.25), 0, 1);
        lift = lift * lift * (3 - 2 * lift);
        LeftGloveT.X = 20 * lift; LeftGloveT.Y = -30 * lift;

        // Three raps: the mitten lunges toward you (bigger) then springs back, with a ripple on the glass.
        double tt = t - 0.25;
        int tap = tt < 0 ? -1 : (int)(tt / 0.2);
        double into = tt < 0 ? 1 : (tt % 0.2) / 0.2;
        bool tapping = tap is >= 0 and < 3;
        double s = 1 + 0.45 * lift + (tapping ? 0.35 * Math.Max(0, 1 - into * 3) : 0);
        LeftGloveS.ScaleX = LeftGloveS.ScaleY = s;
        if (tapping && tap != lastTap) { lastTap = tap; rippleStart = now; }
        return true;
    }

    void RipplePose()
    {
        double k = (now - rippleStart) / 0.5;
        if (k < 0 || k >= 1) { Ripple.Opacity = 0; return; }
        System.Windows.Controls.Canvas.SetLeft(Ripple, 29 + LeftGloveT.X - 10);
        System.Windows.Controls.Canvas.SetTop(Ripple, 106.5 + LeftGloveT.Y - 10);
        RippleS.ScaleX = RippleS.ScaleY = 0.8 + 2.4 * k;
        Ripple.Opacity = 0.85 * (1 - k);
    }

    static void PlayKnock()
    {
        try
        {
            knockWav ??= MakeKnockWav();
            using var player = new SoundPlayer(new MemoryStream(knockWav));
            player.Play(); // async; the stream is read up front
        }
        catch (Exception ex) { Store.Log($"Knock sound: {ex.Message}"); }
    }

    /// <summary>Three soft knuckle-on-glass knocks, synthesised so there's no sound file to ship.</summary>
    static byte[] MakeKnockWav()
    {
        const int rate = 22050;
        int n = (int)(rate * 0.95);
        var samples = new short[n];
        var rnd = new Random(7);
        foreach (double at in new[] { 0.25, 0.45, 0.65 })
        {
            int start = (int)(at * rate);
            for (int i = 0; i < (int)(0.12 * rate) && start + i < n; i++)
            {
                double t = i / (double)rate;
                double env = Math.Exp(-t * 55);
                double v = 0.55 * Math.Sin(2 * Math.PI * 190 * t) + 0.25 * Math.Sin(2 * Math.PI * 410 * t)
                         + 0.2 * (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 300);
                samples[start + i] = (short)Math.Clamp(samples[start + i] + v * env * 9000, short.MinValue, short.MaxValue);
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 2); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(n * 2);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}
