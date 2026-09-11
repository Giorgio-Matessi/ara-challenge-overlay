# ARA Challenge Overlay

An iRacing overlay for the **Almeida Racing Academy** 20-challenge series. It watches the sim,
works out which challenge you've loaded from the track and car, shows the Bronze / Silver / Gold
target times, and pops a banner the moment you set a clean lap quick enough to earn a medal.

## Requirements

- Windows 10/11, [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  (not needed if you grabbed the self-contained release).
- **iRacing must run windowed-borderless, not exclusive fullscreen.** Windows gives an
  exclusive-fullscreen game the whole screen and nothing may draw on top of it. Every sim
  overlay has this limitation; the only workarounds involve hooking the sim's renderer, which
  is not something to point at an anti-cheat.
- The first launch shows a Windows SmartScreen warning, because the exe isn't code-signed:
  **More info → Run anyway**.

## Using it

Only one copy runs at a time — starting a second says so and exits, since two overlays would
stack on screen and fight over the progress file.

Run `AraChallengeOverlay.exe` and leave it running — it lives in the system tray and stays
invisible until iRacing is up.

- On a challenge's track+car: the three target times, a ✓ on every medal you already hold, your
  live lap time and how far the last lap sat from the next tier up.
- On anything else: the track and car ids the sim reported plus the detected conditions, so you
  can check them against `challenges.json`.

Right-click the tray icon for:

| | |
|---|---|
| **Lock position (click-through)** | On by default. Unlock to drag the overlay somewhere else; lock again so the mouse passes through to the sim. |
| **Copy current track/car ID** | Puts the sim's real ids on the clipboard, ready to paste into `challenges.json`. |
| **Exit** | |

A lap is only worth a medal if it's **clean**. Leaving the track surface, picking up incident
points, or touching pit lane invalidates the lap, and the overlay says which one did it.

Your best time and best medal per challenge live in
`%APPDATA%\AraOverlay\progress.json`; window position and lock state in `settings.json` beside it.
The banner only fires when you *improve* a tier, so a second gold lap won't interrupt you again.

## The challenge list

`src/AraOverlay.Core/challenges.json` is embedded into the exe at build time. Each row is keyed
on iRacing's **internal ids**, not the display names:

```json
{
  "number": 19,
  "name": "Le Mans (24 Heures du Mans) — Dallara P217 · WET",
  "trackId": "lemans 24h",        // WeekendInfo:TrackName
  "carId":   "dallarap217",       // DriverInfo:CarPath
  "wet":     true,                // challenges 16-20 only
  "bronze":  "4:13.700",
  "silver":  "4:11.200",
  "gold":    "4:10.200"
}
```

Challenges **16-20 are the wet-weather versions**, so conditions are part of what identifies a
challenge — 14 and 19 are the same car on the same Le Mans layout, 35 seconds apart. The overlay
reads `TrackWetness` and `WeatherDeclaredWet` from the sim and picks the matching version. It
uses two different thresholds on the way up and the way down, so a drying track can't flip the
active challenge back and forth. A wet challenge simply won't match in the dry, which is
deliberate: its targets would be free golds on a dry track.

Those ids aren't reliably guessable from a track or car name, so if a challenge never lights up:
load it in the sim, use the tray's **Copy current track/car ID**, and paste the result over the
row. The tests will catch out-of-order times and duplicate track+car pairs at build time.

## Changing the icon

`src/AraOverlay/AraOverlay.ico` is both the exe icon and the tray icon, built from
`icon-source.png` (the ARA mark on a rounded tile in the overlay's panel colour).

To use different art, replace the `.ico` with any multi-size icon and rebuild. Include a 16x16
frame — that is the size the tray actually draws, and detail disappears at it, so a simple shape
beats a detailed logo. Keep the filename, or update `ApplicationIcon` in `AraOverlay.csproj`.

## Building

```bash
dotnet test                                      # logic only — runs on any OS
dotnet build src/AraOverlay                      # Windows only (WPF)
dotnet publish src/AraOverlay -c Release -r win-x64 --self-contained  # one exe for the league
```

`AraOverlay.Core` holds every decision worth getting right — time parsing, challenge matching,
medal thresholds, lap validity, progress — and has no UI or SDK dependency, so it's covered by
tests that run anywhere. `src/AraOverlay` is the WPF shell and `SdkService.cs` is the only file
that touches iRacing.

## Licence

GPL-3.0, inherited from [IRSDKSharper](https://github.com/mherbold/IRSDKSharper).
