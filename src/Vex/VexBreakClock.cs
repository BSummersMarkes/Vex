using System;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// Break clock: a small digital countdown beside Vex while you're on a timed break, so you can see how long is left
/// without asking. Two kinds:
/// - the 20-20-20 look-away ("Looking away" on the eyes nudge, or [[break: eyes]] in chat): EyeBreakSeconds (20),
///   shown as 0:20 counting down, then a soft chime (with KnockSound on, not in a call), a short dry bubble, and it goes;
/// - lunch (the lunch nudge's "Going", [[break: going to lunch]], Setup → Lunch break): LunchMinutes, as M:SS or
///   H:MM:SS. With LunchRoutine on it simply follows his own lunch (VexLunch.cs) and goes when that ends, early or not;
///   with it off the clock runs on its own, chimes at the end, and goes quietly if you come back early.
/// Drawn inside his own window, so it moves with him, hides when he does and stays out of screen captures.
/// Setup → Behaviour turns it off (BreakClock).
/// </summary>
public partial class VexWindow
{
    enum BreakKind { None, Eyes, Lunch }

    BreakKind breakKind;
    DateTime breakUntil;        // wall clock, so it stays right however slowly frames come (2 fps when unseen)
    bool breakSawAway;          // lunch without his routine: you've gone, so your next input means you're back
    string breakShown = "";
    static byte[]? chimeWav;

    /// <summary>Counting down your lunch (with or without his own lunch routine).</summary>
    bool LunchClockOn => breakKind == BreakKind.Lunch;

    /// <summary>You're looking away for 20 seconds: show the countdown. Doesn't replace a lunch countdown.</summary>
    public void StartEyeBreak()
    {
        if (LunchClockOn) return;
        StartBreakClock(BreakKind.Eyes, Math.Max(5, Store.Config.EyeBreakSeconds));
    }

    void StartBreakClock(BreakKind kind, double seconds)
    {
        if (!Store.Config.BreakClock || (kind == BreakKind.Lunch && LunchClockOn)) return;   // "Going" twice doesn't restart it
        breakKind = kind;
        breakUntil = DateTime.Now.AddSeconds(seconds);
        breakSawAway = false;
        breakShown = "";
    }

    /// <summary>Takes the clock away. <paramref name="cue"/>: time's up, so the soft chime goes with it.</summary>
    void StopBreakClock(bool cue)
    {
        if (breakKind == BreakKind.None) return;
        breakKind = BreakKind.None;
        BreakClock.Visibility = Visibility.Collapsed;
        if (cue && Store.Config.KnockSound && Services.Hub.InCall == null) PlayChime();
    }

    /// <summary>Called every frame: ticks the display, ends it when time's up, and keeps it on the roomier side of him.</summary>
    void UpdateBreakClock(Forms.Screen scr)
    {
        if (breakKind == BreakKind.None) return;
        var left = breakUntil - DateTime.Now;

        if (breakKind == BreakKind.Lunch && AtLunch) { }   // his own lunch ends it (WakeFromLunch), with his own wake-up line
        else if (left <= TimeSpan.Zero)
        {
            bool eyes = breakKind == BreakKind.Eyes;
            StopBreakClock(cue: true);
            Say(eyes ? Pick("Done. Back to it.", "Twenty seconds. Eyes reset.", "That'll do. Back to the screen.")
                     : Pick("Lunch is up. Back to it.", "That's lunch. The desk missed you. I didn't."), 4, important: true);
            return;
        }
        else if (breakKind == BreakKind.Lunch)
        {
            // No routine to wake from, so the same "back early" rule as his lunch: gone a while, then input again.
            var idle = Native.IdleTime();
            if (Services.Hub.Presence.Locked || idle.TotalMinutes >= Store.Config.LunchAwayMinutes) breakSawAway = true;
            else if (breakSawAway && idle.TotalSeconds < 2) { StopBreakClock(cue: false); return; }
        }

        // Rounded up, so it reads 0:20 at the start and only shows 0:00 for the instant it ends.
        int secs = (int)Math.Max(0, Math.Ceiling(left.TotalSeconds));
        string text = secs >= 3600 ? $"{secs / 3600}:{secs / 60 % 60:00}:{secs % 60:00}" : $"{secs / 60}:{secs % 60:00}";
        if (text != breakShown) { breakShown = text; BreakClockText.Text = text; }
        if (BreakClock.Visibility != Visibility.Visible) { BreakClock.Visibility = Visibility.Visible; BreakClock.UpdateLayout(); }

        // Beside him on the side facing the middle of his screen: in a corner (at lunch) the other side is off the edge.
        bool rightSide = x < (scr.WorkingArea.Left + scr.WorkingArea.Right) / 2.0;
        double gap = BodyHalfW - 16;   // his hem is narrower than BodyHalfW; this sits it just off his side
        Canvas.SetLeft(BreakClock, rightSide ? RigCx + gap : RigCx - gap - BreakClock.ActualWidth);
    }

    static void PlayChime()
    {
        try
        {
            chimeWav ??= MakeChimeWav();
            using var player = new SoundPlayer(new MemoryStream(chimeWav));
            player.Play(); // async; the stream is read up front
        }
        catch (Exception ex) { Store.Log($"Chime sound: {ex.Message}"); }
    }

    /// <summary>
    /// A quiet two-note chime (a fifth apart, bell-like overtones, fading over a second). Synthesised, like the knock,
    /// so there's no sound file to ship; kept softer than the knock since it only says "time's up".
    /// </summary>
    static byte[] MakeChimeWav()
    {
        const int rate = 22050;
        int n = (int)(rate * 1.3);
        var samples = new short[n];
        foreach (var (at, f) in new[] { (0.0, 880.0), (0.18, 1318.5) })
        {
            int start = (int)(at * rate);
            for (int i = 0; start + i < n; i++)
            {
                double t = i / (double)rate;
                double env = Math.Exp(-t * 4.5) * Math.Min(1, t * 400);   // soft attack, so it doesn't click
                double v = Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(2 * Math.PI * f * 2.76 * t) * Math.Exp(-t * 6);
                samples[start + i] = (short)Math.Clamp(samples[start + i] + v * env * 3200, short.MinValue, short.MaxValue);
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        static byte[] Tag(string s) => System.Text.Encoding.ASCII.GetBytes(s);   // (not "RIFF"u8: the lite build's .NET Framework lacks it)
        w.Write(Tag("RIFF")); w.Write(36 + n * 2); w.Write(Tag("WAVE"));
        w.Write(Tag("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Tag("data")); w.Write(n * 2);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}
