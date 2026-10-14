using System;
using System.Windows;

namespace Vex;

/// <summary>
/// Frame rate. Every frame redraws his whole (blurred, see-through) figure, which is what costs CPU, so he only runs
/// at full speed while something visibly moves: walking, being carried, knocking, blinking, juggling, an activity
/// easing in or out, eyes following the mouse. Standing about, reading, sitting or napping he gets by on a third of
/// that (breathing at 10 fps looks the same), and when nobody can see him (hidden, full screen, PC locked) he just
/// ticks over so he notices when he's needed again.
/// </summary>
public partial class VexWindow
{
    static readonly TimeSpan FastFrame = TimeSpan.FromMilliseconds(33);
    static readonly TimeSpan CalmFrame = TimeSpan.FromMilliseconds(100);
    static readonly TimeSpan UnseenFrame = TimeSpan.FromMilliseconds(500);

    /// <summary>Set by Animate and Settle: a smoothed value (head turn, eyes, height) is still on its way to where it's going.</summary>
    bool settling;
    int paceCursorX, paceCursorY;

    /// <summary>Picks the next frame's interval. Called at the end of every frame.</summary>
    void Pace()
    {
        var next = Unseen ? UnseenFrame : Lively() ? FastFrame : CalmFrame;
        if (timer.Interval != next) timer.Interval = next;
        settling = false;
    }

    /// <summary>Back to full speed now, for things that start between frames (a click on him, a knock).</summary>
    void Hurry()
    {
        if (timer.Interval != FastFrame) timer.Interval = FastFrame;
    }

    bool Unseen => hidden || autoHidden || Services.Hub.Presence.Locked;

    bool Lively()
    {
        if (mode is not (Mode.Idle or Mode.Sleeping) || pressed || settling || thinking) return true;
        if (Knocking || now - rippleStart < 0.5 || now < squashUntil) return true;
        if (mode != Mode.Sleeping && (blinkStart >= 0 || now >= nextBlink - CalmFrame.TotalSeconds)) return true;   // asleep, no blinks

        // The mouse moving near him: his eyes follow it.
        if (Native.GetCursorPos(out var c) && (c.X != paceCursorX || c.Y != paceCursorY))
        {
            paceCursorX = c.X; paceCursorY = c.Y;
            double dx = c.X - x, dy = c.Y - (y - EyeY * S);
            if (dx * dx + dy * dy < 700 * S * (700 * S)) return true;
        }
        return act != Act.None && !CalmAct();
    }

    /// <summary>The slow middle of an activity, after it has eased in and before it eases out.</summary>
    bool CalmAct()
    {
        if (act == Act.Lunch) return LunchNapping && !lunchRoused && now - eatEnd > 2;   // eating chews; the nap just breathes
        if (act is not (Act.Read or Act.Phone or Act.Sit)) return false;                    // juggling, coding, quick ones
        if (now < actStart + 2.2 || now > actEnd - 2) return false;                         // easing in or out (Sit's book or phone too)
        bool reading = act == Act.Read || sitSub == Act.Read;
        return !reading || (flipStart < 0 && now < nextFlip - CalmFrame.TotalSeconds);      // turning a page
    }
}
