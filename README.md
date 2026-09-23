# ARA Challenge Overlay

An iRacing overlay for the **Almeida Racing Academy** challenges — Academy, Weekly, and whatever
else ARA publishes. It watches the sim, works out which challenge you've loaded from the track and
car, shows the Bronze / Silver / Gold
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

Run `AraChallengeOverlay.exe` and leave it running — it lives in the system tray and shows the
ARA mark until iRacing is up, so you can unlock it and drag it into place before a session.

On a challenge's track and car, the panel reads top to bottom:

| Row | |
|---|---|
| **Category · number** | Which challenge you've loaded, e.g. `ACADEMY CHALLENGES · 3`. Wet sessions are marked. |
| **Goal / est. lap** | The tier you're chasing next and its target, against a live projection of the lap you're on. Once the lap is spoiled this cell says what spoiled it instead. |
| **The big number** | How far your last lap sat from the goal. Red and `▼` means there's still time to find; green and `▲` means you cleared it. |
| **Last lap / session best** | Your last completed lap, and the quickest clean one this session. |
| **Gold / silver / bronze** | All three targets, with a ✓ on every medal you already hold. |

On anything else it shows the track and car ids the sim reported plus the detected conditions.
Before your first sign-in it has no challenges at all and says so.

Right-click the tray icon for:

| | |
|---|---|
| **Lock position (click-through)** | On by default. Unlock to drag the overlay somewhere else; lock again so the mouse passes through to the sim. **Ctrl+Alt+L** does the same without leaving the sim. |
| **Exit** | |

Ctrl+Alt+L is registered system-wide, because the overlay never takes keyboard focus. If another
program already owns that combination the registration fails, the tray menu stops showing the
shortcut, and the menu item still works.

A lap is only worth a medal if it's **clean**. Leaving the track surface, picking up incident
points, or touching pit lane invalidates the lap, and the overlay says which one did it.

Your best time and best medal per challenge live in
`%APPDATA%\AraOverlay\progress.json`; window position and lock state in `settings.json` beside it.
If the overlay ever stops responding to the sim, `errors.log` in the same folder records what the
iRacing SDK threw — send it along with the report.
The banner only fires when you *improve* a tier, so a second gold lap won't interrupt you again.

## The challenge list

Challenges come from the ARA Labs API (`/api/v1/garage61/training-plans`) after you sign in from
the tray. Each training plan is a category, and its name is what the panel shows. The last good
list is cached in `%APPDATA%\AraOverlay\challenges.cache.json`, so the overlay keeps working
offline and through an expired session. Nothing is embedded in the exe.

Challenges match on iRacing's numeric track and car ids. Where one plan uses the same track and
car twice, that is its wet and dry pair and the slower targets are the wet ones; the overlay picks
between them from the sim's live track wetness. If one track and car belongs to more than one plan,
the tray menu lets you choose which challenge you're running.

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
