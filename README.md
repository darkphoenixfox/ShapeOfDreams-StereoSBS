# Stereo SBS for Shape of Dreams

A Shape of Dreams mod that renders the game in side-by-side (SBS) stereoscopic 3D, for 3D TVs, monitors, projectors and VR headsets that can play SBS video.

**Status: experimental.**

## What it does

- Renders the main camera from two eye positions using off-axis projections, so there is no toe-in and no vertical parallax.
- **Half SBS** (default) squeezes each eye into half the screen width, which is what most 3D TVs and players expect. **Full SBS** keeps each eye's proportions, with a narrower field of view.
- Auto convergence keeps the character the camera follows at screen depth, and eye separation scales with camera zoom.
- The game's UI is drawn into both eyes, squeezed the same way as the world, so health bars and nametags line up.
- Hides the game's cursor and draws it once per eye, at the depth of the ground under it.
- Client-side only. It does not change gameplay.

## How it works

Each full-screen camera gets two hidden clone cameras that draw the left and right halves of the screen. The original camera is swapped out of URP's render list every frame, but its state is never modified. The game's own screen math (aiming rays, `WorldToScreenPoint` for health bars, UI raycasts) therefore keeps working on the normal full-screen camera.

## Settings

| Setting | Default | Description |
|---|---|---|
| Layout | Half SBS | Half or Full side-by-side. |
| Swap eyes | off | For cross-eyed viewing, or if depth looks inside-out. |
| Depth strength | 1 | Eye separation. 1 = 1/30 of the distance to the followed character. 0 = flat. |
| Auto convergence | on | Puts the screen plane at the followed character. |
| Convergence scale | 1 | Below 1 the character sinks into the screen, above 1 it pops out. |
| Manual convergence distance | 20 | Used when auto convergence is off or nothing is followed. |
| Draw cursor in both eyes | on | Stereo software cursor. |
| Cursor depth follows ground | on | Cursor depth matches the ground under it; flat over UI. |
| Move overlay UI into both eyes | on | Moves Screen Space Overlay canvases onto the UI camera. |

Console command `sbs_dump` logs every camera and root canvas to `Player.log`, for debugging.

## Known limitations

- In Full SBS mode, the UI and cursor are not aligned with the world. Use Half SBS.
- The developer FPS counter only appears once, across the whole screen.
- Every frame is rendered twice, so expect roughly double the GPU cost.

## Building

1. Clone this repository into the game's `Mods` folder, so that the path is `Shape of Dreams/Mods/ShapeOfDreams-StereoSBS/`. The project references the game's DLLs at `..\..\Shape of Dreams_Data\Managed`.
2. Run `dotnet build`. The mod loads `bin/Debug/netstandard2.1/StereoSBS.dll`, as listed in `about/metadata.json`.

Built from the official [mod template](https://github.com/LizardSmoothie/ShapeOfDreamsModDocs).
