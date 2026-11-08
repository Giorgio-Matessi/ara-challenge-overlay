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


Your best time and best medal per challenge live in
`%APPDATA%\AraOverlay\progress.json`; window position and lock state in `settings.json` beside it.
If the overlay ever stops responding to the sim, `errors.log` in the same folder records what the
iRacing SDK threw — send it along with the report.
The banner only fires when you *improve* a tier, so a second gold lap won't interrupt you again.

## Building

```bash
dotnet build src/AraOverlay                      # Windows only (WPF)
dotnet publish src/AraOverlay -c Release -r win-x64 --self-contained  # one exe for the league
```

## Licence

GPL-3.0, inherited from [IRSDKSharper](https://github.com/mherbold/IRSDKSharper).
