# My City

An endless, randomly generated city you can drive through forever, with textured brick, stone, office and glass buildings. You drive a taxi: pick up fares, drop them off and earn money, among AI traffic and people on the sidewalks. Runs on PC (keyboard, mouse, gamepad) and Android (on-screen touch buttons). Published on the F-Droid repo as `com.Tony3974.MyCity`.

## Tools and versions

| What | Version / detail |
|---|---|
| Unity | 6000.0.85f1 (Unity Hub install at `~/Unity/Hub/Editor/6000.0.85f1`) |
| Render pipeline | URP 17.0.4 (`Assets/Settings`: `PC_RPAsset` for desktop, `Mobile_RPAsset` for Android) |
| Input | Input System 1.20 only (old Input Manager is off), read directly with `Keyboard.current`, `Gamepad.current`, `Mouse.current`, EnhancedTouch |
| Android | Unity's bundled SDK, NDK and OpenJDK; IL2CPP, ARM64, min API 26, landscape, Vulkan with a GLES3 fallback |
| Phone debugging | `adb` (`adb logcat -s Unity:V` shows Unity's log) |
| Publishing | `~/fdroid/release.sh` (fdroidserver, uploads `repo/` to the VPS) |

## What's built

### Endless city (built while you play)
The city isn't saved in the scene. It's generated in the game, in square tiles of 8 × 8 blocks (416 m).

- `Assets/Scripts/City/CityStreamer.cs` (on the **City Streamer** object) keeps a 3×3 grid of tiles around the car.
  - New tiles are generated on a worker thread, then built a few blocks per frame (`buildBudgetMs`), so driving doesn't stutter. Tiles that fall out of the grid are unloaded.
  - The grid only moves after the car is 30 m into a new tile (`recenterMargin`), so driving along a border doesn't load and unload tiles over and over.
  - When the car gets 4 km from (0,0,0), everything moves back by whole tiles (`originShiftDistance`). Without this, floats lose precision and the car starts to shake.
  - A big flat ground follows the car under the city, so the edge of the loaded area fades into fog instead of showing sky.
  - On start it builds the taxi (unless a car is assigned) and puts it in a lane near the middle of tile (0,0).
- `Assets/Scripts/City/CityGenerator.cs` is pure C#, which is why it can run off the main thread.
  - The same seed and tile always give the same city, so driving back shows the same streets. Tiles line up because each one has roads only on its west and south edges.
  - Smooth noise decides how "downtown" each block is: towers cluster in downtowns with low-rise in between. There's always a downtown around the start.
  - Each block is **one mesh** (one submesh per material) with box colliders, about 70 objects per tile. The old pre-built city was about 2,300 objects.
  - Every building has shops on the ground floor. Low-rise buildings are brick (red or tan), stone, plaster (white or grey, some with shutters) or offices. Towers are glass (4 tints), offices or stone, stacked in setback tiers with a plant room and sometimes an antenna.
  - Building heights snap to whole floors (3.5 m, shops 4.5 m), and each face gets a whole number of 3 m window bays, so windows never get cut off.
  - Roads have yellow centre dashes, zebra crosswalks, and mast-arm traffic lights at every intersection. Parks have paths and trees.
- All the settings (seed, block size, heights, park chance, downtown size) are on the City Streamer component.

### Textures
`Assets/Editor/CityTextureBaker.cs` draws every texture in code; nothing is downloaded. Use **Tools → City → Bake Textures and Materials**.
- It writes PNGs plus normal maps to `Assets/City/Textures/` and URP Lit materials `City_*.mat` to `Assets/City/Materials/`.
- Smoothness is stored in each texture's alpha, so glass is shiny and walls are matte.
- Facades are drawn to the bay and floor sizes in `CityLook` (in `CityGenerator.cs`). If you change those sizes, rebake.
- **Tools → City → Set Up Scene** rebakes everything, removes any old pre-built city, adds or refreshes the City Streamer, and sets the fog (linear, 80–420 m, matched to the sky at the horizon) and the camera's far clip (700 m).

### Car
- `Assets/Scripts/CarController.cs`: an arcade raycast car.
  - Each wheel is a spring, so the car rides up curbs.
  - Sideways grip per wheel, with sweeping handbrake drift physics (moderated rear grip, yaw damping and slip-angle assist so drifts stretch around corners instead of snapping into U-turns).
  - Speed-sensitive steering: both maximum steering angle and turn-in rate taper down as speed climbs.
  - All tuning values are on the component in the Inspector. They are the starting car's (86 km/h, loose grip); upgrades raise them.
- `Assets/Scripts/CrashFeedback.cs` (added by the car): sparks where you hit something, camera shake, and a split-second slow motion on hard hits. Scraping along a wall throws a stream of sparks.
- `Assets/Scripts/CarDamage.cs` (added by the car): tracks car health (0–100) from side crashes and wall scrapes, deforms bumpers/panels, scuffs paint, knocks out headlights/taillights, and emits hood smoke and flames when heavily damaged. Shows a health bar under the minimap; when wrecked, the engine dies and you can repair ($20) or take a free replacement if broke.
- `Assets/Scripts/PlayerCar.cs`: the taxi is built in code when you press Play (the City Streamer calls it). It's made of boxes and cylinders: a yellow body with recessed wheel wells and a black stripe, a glass cabin, bumpers, lights, a roof light bar with blocky placeholder lettering (lit while the taxi is free), and plus-spoke wheels that steer and spin. Its materials are tinted copies of one city material, so the build needs no extra shaders. The City Streamer places it on a road and points the camera at it. To drive a different car, drag it into the City Streamer's **Car** field.

### Camera
`Assets/Scripts/ThirdPersonCamera.cs`, on the Main Camera:
- Smooth follow, orbit and zoom.
- Swings back behind the car about 1.5 s after you stop moving the camera.
- Pulls in when a building gets in the way (cars and other rigidbodies are ignored).
- Field of view widens a little with speed.

### Taxi game
`Assets/Scripts/Taxi/` creates itself at startup:
- `TaxiGame.cs`: people with a green light over their head want a ride. Stop next to one to pick them up, then drive to the yellow beacon 4–8 blocks away and stop there to get paid ($25). Other waiting fares hide their green lights while you are carrying a passenger. An arrow over the taxi points at the nearest fare or the drop-off. Money and trip count are saved in `PlayerPrefs`. Shows the money and what to do next at the top of the screen.
- `Assets/Scripts/UpgradeMenu.cs` (added by TaxiGame): spend fare money on speed, brakes and handling, five levels each ($50, $100, $150, $250, $400), or repair car damage. Fully upgraded the car does 137 km/h with better grip and steering than the start. Levels are saved in `PlayerPrefs` and applied to the CarController. Opened with the UPGRADES button (top-right) and pauses the game while open.
- `Pedestrians.cs`: about 40 blocky walkers loop around the blocks near you, turning back or stopping now and then, and 5 fares wait at the curb. People who end up far away are moved to a block near you.

### Traffic
`Assets/Scripts/Traffic/Traffic.cs` creates itself at startup. About 48 sedans, SUVs and detailed delivery vans drive in the right-hand lanes and pick a random way at each intersection. They stop for the visible red/yellow/green traffic lights (drawn in 3 batched meshes synced to each intersection's cycle), queue behind the car in front (yours too), and wait for oncoming cars before turning left. Brake lights come on when they slow down. Each one is a physics body held on its lane, so you can shove it. Knock it more than about 2 m off its lane and it tumbles free until it's out of sight, then it's reused. Respawning is biased ahead of the player and cars far behind are recycled quickly so straight avenues stay populated.

### HUD
`Assets/Scripts/Hud/` creates itself at startup and draws with OnGUI:
- `Speedometer.cs`: a round dial with a ring of lights that fill with speed (red past 120 km/h), a needle, the speed in digits and an R when reversing. Bottom-centre on phones, bottom-right otherwise.
- `Minimap.cs`: a round map in the top-left, turned so the camera's view is up. It's drawn into a 160 px texture every frame from the road grid, with no second camera: roads, sidewalks, buildings (lighter downtown) and parks (`CityGenerator.IsPark`). It shows traffic, fares, the drop-off (pinned to the rim when it's off the map) and north.
- `GameMetrics.cs`: a toggleable live stats panel (`M`/`F3` or the `STATS` button under `UPGRADES`) tracking 5-minute fares/5m, money/min, average delivery time, distance driven, top speed, total and longest drift score (plus a live `DRIFT +...` banner while drifting), crashes/damage taken, and nearby/ahead traffic density.
- `HudDraw.cs`: the soft circles, bars, needle, arrow and hover-safe text helper, made in code.

### Menus
`Assets/Scripts/GameMenu.cs` creates itself at startup. The game opens on a title screen (PLAY/CONTINUE, SETTINGS, QUIT) while the city and traffic run behind it and the camera circles the taxi; the car takes no input until PLAY. The pause menu stops time and offers resume, upgrades, stats, flip car, settings, main menu and quit (PC only). Settings (saved in `PlayerPrefs`): render quality (scales the URP render scale, restored when Play mode ends), frame rate cap, invert camera Y, touch button size, and reset progress. Other scripts check `GameMenu.BlocksInput`, `GameMenu.HideHud` and `GameMenu.Frozen`; `GameMenu.ApplyTimeScale()` is the one place that sets `Time.timeScale` back.

### Touch controls
`Assets/Scripts/TouchControls.cs` creates itself on touchscreen devices, so nothing needs to be in the scene. It draws its buttons with OnGUI.

### Controls

| | Keyboard / mouse | Gamepad | Touch |
|---|---|---|---|
| Gas / brake / reverse | W / S, ↑ ↓ | RT / LT | GAS / BRAKE |
| Steer | A / D, ← → | Left stick | LEFT / RIGHT |
| Handbrake (drift) | Space | A | DRIFT |
| Flip upright / repair when wrecked | R | Y (or A when wrecked) | RESET (or REPAIR button) |
| Orbit camera | Hold right mouse + drag | Right stick | Drag on an empty part of the screen |
| Zoom | Scroll | Bumpers | none |
| Pause menu | Esc or P, or click the pause button | Start (B closes) | Pause button or back |
| Upgrades menu | U (Esc closes), click UPGRADES, or from the pause menu | Select/View (B closes) | UPGRADES (back closes) |
| In a menu: pick / choose | ↑ ↓ / Enter or Space, or click | D-pad / A | Tap |
| Metrics overlay | M or F3, or click STATS | none | STATS |

### Scene
`Assets/Scenes/SampleScene.unity` holds the City Streamer, the camera, the light and the post-processing volume. The city and the taxi only exist in Play mode. It's the only scene in the build.

## Building for Android

With Unity closed:

```bash
Tools/android-build.sh            # builds Builds/Android/MyCity.apk (log: Logs/android-build.log)
Tools/android-build.sh --install  # also installs and starts it on a phone plugged in over USB
```

With Unity open, use **Tools → Android → Build APK**. **Tools → Android → Apply Android Settings** sets the package id, IL2CPP, ARM64, orientation and the rest. That code is in `Assets/Editor/AndroidBuild.cs`.

For a development build that reports file and line numbers in stack traces, and logs `CITY ...` lines (frame rate every 5 s, how long each tile took to build, origin shifts) that you can read with `adb logcat -s Unity:V | grep CITY`. It also logs a `HITCH` line for any frame over 40 ms, with the engine steps that took the time (garbage collection, HUD drawing, physics, destroys, tile building), from `Assets/Scripts/City/HitchLogger.cs`:

```bash
~/Unity/Hub/Editor/6000.0.85f1/Editor/Unity -batchmode -buildTarget Android -projectPath "$PWD" \
  -executeMethod AndroidBuild.CommandLine -cityDev -cityOut Builds/Android/MyCity-dev.apk \
  -logFile Logs/android-build-dev.log
```

## Releasing to F-Droid

1. Bump the version in `ProjectSettings/ProjectSettings.asset` (or Player Settings). Raise `AndroidBundleVersionCode` by 1 and change `bundleVersion`. Phones only update when the version code goes up. The current release is 0.6.1, version code 9.
2. Build with `Tools/android-build.sh`.
3. `cp Builds/Android/MyCity.apk ~/APKs/MyCity.apk`
4. `~/fdroid/release.sh`

The APK is signed with Unity's debug key on this machine. Future updates need to be signed with the same key, or phones will refuse to install them over the current version.

## Gotchas (learned the hard way)

- **Batch Android builds must use `-buildTarget Android`.** Without it, Unity starts on the PC platform and URP strips every shader. The phone shows the splash and then a grey screen, and logcat shows a `NullReferenceException` in `PostProcessPass.RenderPostProcessingRenderGraph`. To check a build, find `"Universal Render Pipeline/Lit" pass "ForwardLit"` in the build log: "After scriptable stripping" must not be 0. (Scoop & Sausage's `android-build.sh` doesn't pass this flag yet.)
- **Fog must be on in the scene, not only from code.** URP strips fog shader variants when no scene in the build has fog turned on, so fog switched on only at runtime does nothing on the phone.
- **Big flat meshes are split into a grid** (ground in 16 m squares, horizon in 100 m squares). URP works out fog per vertex, so one huge quad would fog the area near the camera wrongly.
- **Materials made only in code need a shader that's in the build.** URP strips shaders no scene material uses. So the glow (beacons, arrow, fare lights, brake lights) and the sparks use materials in `Assets/Resources/`, which always go into the build. Everything else is a tinted copy of a city material.
- **Tile generation must not make garbage.** Each tile's mesh lists are several MB; when they were thrown away after every tile, the garbage collector stalled the phone for 70-220 ms at each tile crossing. `CityGenerator` now pools `TileData` (`Recycle` after the tile is built) and CityStreamer takes unloaded tiles apart 12 objects per frame.
- **Meshes made at runtime must be destroyed by hand.** Destroying a GameObject doesn't free its Mesh. CityStreamer keeps a list for each tile and destroys them when it unloads the tile.
- **Performance (Galaxy S23 Ultra, old pre-built city):** about 9 copies of the old 2,300-object map ran at 120 fps, 16 at 60 fps and 49 at 30 fps. The processor ran out before the GPU. The new city is about 70 objects per tile.

## Ideas for later

- Night mode with lit windows (a glow map on the facades)
- Streetlights
- Simpler versions of far blocks (LOD) if mid-range phones struggle
