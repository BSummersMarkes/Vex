# Vex for Mac

A little character in a red hoodie who lives at the bottom of your screen, just above the Dock. He wanders about, blinks, dozes off when you've been away, and now and then reminds you to stand up, stretch and drink some water. This is an early version: Ben's adding more.

Needs **macOS 14 (Sonoma) or newer**. On a Mac with an Apple chip (M1 or later), it runs through Apple's Rosetta: if the Mac asks to install Rosetta the first time, say yes.

## Getting him going

1. Double-click **Vex-mac.zip** to unzip it, then drag **Vex** into your **Applications** folder.
2. Open **Vex** from Applications. The first time, the Mac will refuse with *"Apple could not verify 'Vex' is free of malware"* (or *"…developer cannot be verified"*). That's because it's a home-made app, not one from the App Store. Click **Done**.
3. Open **System Settings → Privacy & Security**, scroll down to the message about Vex, and click **Open Anyway**. Enter your password if asked, then **Open Anyway** once more.

From then on he opens normally. He has no Dock icon: look for his little hood in the menu bar at the top of the screen.

> **Ben, the Terminal way instead of step 3:** `xattr -dr com.apple.quarantine /Applications/Vex.app`, then open him.

## Using him

| | |
|---|---|
| **Click him** | How long you've been sat, and today's coffee count |
| **Drag him** | Pick him up and drop him somewhere else. He's not keen |
| **Click his mug** | When you refill your own coffee. It empties over about 45 minutes |
| **Right-click him**, or his **menu-bar icon** | Taking a break, I refilled my coffee, have a nap, **Start at login**, Quit |

He nudges you to stretch after about 55 minutes of sitting, and about water every couple of hours, never more than once every 15 minutes. Being away from the Mac for 15 minutes or more counts as a break.

## Privacy

Everything stays on the Mac, in `~/Library/Application Support/Vex`. Nothing is sent anywhere. He only knows how long since the keyboard or mouse was last used, not what you're doing.

## Removing him

Right-click him, untick **Start at login**, then **Quit**. Drag **Vex** from Applications to the Bin, and delete `~/Library/Application Support/Vex` if you like.
