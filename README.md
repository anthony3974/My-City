# My City

An endless, randomly generated city you can drive through forever, with textured brick, stone, office and glass buildings, a drivable car and a third-person camera. Runs on PC (keyboard, mouse, gamepad) and Android (on-screen touch buttons). Published on the F-Droid repo as `com.Tony3974.MyCity`.

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
  - On start it moves the car into a lane near the middle of tile (0,0).
- `Assets/Scripts/City/CityGenerator.cs` is pure C#, which is why it can run off the main thread.
  - The same seed and tile always give the same city, so driving back shows the same streets. Tiles line up because each one has roads only on its west and south edges.
  - Smooth noise decides how "downtown" each block is: towers cluster in downtowns with low-rise in between. There's always a downtown around the start.
  - Each block is **one mesh** (one submesh per material) with box colliders, about 70 objects per tile. The old pre-built city was about 2,300 objects.
  - Every building has shops on the ground floor. Low-rise buildings are brick (red or tan), stone, plaster (white or grey, some with shutters) or offices. Towers are glass (4 tints), offices or stone, stacked in setback tiers with a plant room and sometimes an antenna.
  - Building heights snap to whole floors (3.5 m, shops 4.5 m), and each face gets a whole number of 3 m window bays, so windows never get cut off.
  - Roads have yellow centre dashes and zebra crosswalks. Parks have paths and trees.
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
  - Sideways grip per wheel, with less grip on the rear wheels when the handbrake is on, for drifting.
  - Steering gets tighter at speed.
  - Speed readout in km/h.
  - All tuning values are on the component in the Inspector.
- `Assets/Editor/CarSpawner.cs`: **Tools → Spawn Car** builds the car out of primitives (red body, glass cabin, bumpers, lights, wheels that steer and spin). It hooks up the Main Camera. Where you spawn it doesn't matter, because the City Streamer moves the car onto a road when you press Play.

### Camera
`Assets/Scripts/ThirdPersonCamera.cs`, on the Main Camera:
- Smooth follow, orbit and zoom.
- Swings back behind the car about 1.5 s after you stop moving the camera.
- Pulls in when a building gets in the way.
- Field of view widens a little with speed.

### Touch controls
`Assets/Scripts/TouchControls.cs` creates itself on touchscreen devices, so nothing needs to be in the scene. It draws its buttons with OnGUI.

### Controls

| | Keyboard / mouse | Gamepad | Touch |
|---|---|---|---|
| Gas / brake / reverse | W / S, ↑ ↓ | RT / LT | GAS / BRAKE |
| Steer | A / D, ← → | Left stick | LEFT / RIGHT |
| Handbrake (drift) | Space | A | DRIFT |
| Flip upright | R | Y | RESET |
| Orbit camera | Hold right mouse + drag | Right stick | Drag on an empty part of the screen |
| Zoom | Scroll | Bumpers | none |

### Scene
`Assets/Scenes/SampleScene.unity` holds the City Streamer, the car, the camera, the light and the post-processing volume. The city itself only exists in Play mode. It's the only scene in the build.

## Building for Android

With Unity closed:

```bash
Tools/android-build.sh            # builds Builds/Android/MyCity.apk (log: Logs/android-build.log)
Tools/android-build.sh --install  # also installs and starts it on a phone plugged in over USB
```

With Unity open, use **Tools → Android → Build APK**. **Tools → Android → Apply Android Settings** sets the package id, IL2CPP, ARM64, orientation and the rest. That code is in `Assets/Editor/AndroidBuild.cs`.

For a development build that reports file and line numbers in stack traces, and logs `CITY ...` lines (frame rate every 5 s, how long each tile took to build, origin shifts) that you can read with `adb logcat -s Unity:V | grep CITY`:

```bash
~/Unity/Hub/Editor/6000.0.85f1/Editor/Unity -batchmode -buildTarget Android -projectPath "$PWD" \
  -executeMethod AndroidBuild.CommandLine -cityDev -cityOut Builds/Android/MyCity-dev.apk \
  -logFile Logs/android-build-dev.log
```

## Releasing to F-Droid

1. Bump the version in `ProjectSettings/ProjectSettings.asset` (or Player Settings). Raise `AndroidBundleVersionCode` by 1 and change `bundleVersion`. Phones only update when the version code goes up. The current release is 0.1.2, version code 3.
2. Build with `Tools/android-build.sh`.
3. `cp Builds/Android/MyCity.apk ~/APKs/MyCity.apk`
4. `~/fdroid/release.sh`

The APK is signed with Unity's debug key on this machine. Future updates need to be signed with the same key, or phones will refuse to install them over the current version.

## Gotchas (learned the hard way)

- **Batch Android builds must use `-buildTarget Android`.** Without it, Unity starts on the PC platform and URP strips every shader. The phone shows the splash and then a grey screen, and logcat shows a `NullReferenceException` in `PostProcessPass.RenderPostProcessingRenderGraph`. To check a build, find `"Universal Render Pipeline/Lit" pass "ForwardLit"` in the build log: "After scriptable stripping" must not be 0. (Scoop & Sausage's `android-build.sh` doesn't pass this flag yet.)
- **Fog must be on in the scene, not only from code.** URP strips fog shader variants when no scene in the build has fog turned on, so fog switched on only at runtime does nothing on the phone.
- **Big flat meshes are split into a grid** (ground in 16 m squares, horizon in 100 m squares). URP works out fog per vertex, so one huge quad would fog the area near the camera wrongly.
- **Meshes made at runtime must be destroyed by hand.** Destroying a GameObject doesn't free its Mesh. CityStreamer keeps a list for each tile and destroys them when it unloads the tile.
- **Performance (Galaxy S23 Ultra, old pre-built city):** about 9 copies of the old 2,300-object map ran at 120 fps, 16 at 60 fps and 49 at 30 fps. The processor ran out before the GPU. The new city is about 70 objects per tile.
- **App icon:** there isn't one yet, so it's Unity's default icon.

## Ideas for later

- App icon
- AI traffic or pedestrians
- Night mode with lit windows (a glow map on the facades)
- Streetlights and traffic lights
- Simpler versions of far blocks (LOD) if mid-range phones struggle
