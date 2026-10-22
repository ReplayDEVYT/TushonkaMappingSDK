# CustomMapLoader

A mod that loads custom Escape from Tarkov maps in SPT 4.1.6, and the Unity SDK for building them. Documentation is written by AI, but still matches what you should be doing. I've tested all of this, no worries.

```
BepInEx/plugins/CustomMapLoader/bundles/maps/<id>.bundle, <id>_scenes.bundle
SPT_Runtime/user/mods/CustomMapLoader/maps/<id>/base.json, locale.json, sdk_export.json
```

**Author:** mizmii
**Version:** 1.0.0
**SPT Version:** 4.1.6 (client Unity 2022.3.43f1)
**License:** Unlicense

It has four parts:

| Part | Folder | What it does |
| --- | --- | --- |
| Unity SDK | `Unity/TushonkaMappingSDK` | Field-only stubs of the game's map components, Tarkov's layers/tags/physics/navmesh settings, scene tools, and a map bundle builder. |
| Client plugin | `Client/` | Serves map bundles from its plugin folder and generates BSG's AI navigation data from the navmesh at raid start. |
| Server mod | `Server/` | Registers every map in `maps/<id>/` as a new location. |
| Stub generator | `Tools/StubGenerator` | Regenerates the Unity stubs from `Assembly-CSharp.dll` after a game update. |
| Shader stand-ins | `Tools/ShaderStandins` | Regenerates the editor stand-ins for the game's shaders from its shader bundle. |

The map loading and AI generation approach comes from [ManimalIcebreaker](https://github.com/danauraborealis/ManimalIcebreaker) by danauraborealis, used with permission.

## Quick start

1. Build and install CustomMapLoader:

   ```sh
   dotnet build CustomMapLoader.sln -c Release -p:DeployToSpt=true
   ```

   The client build copies its DLL to `C:\spt41\BepInEx\plugins\CustomMapLoader\`. `DeployToSpt=true` copies the server mod and its `maps/` folder to `C:\spt41\SPT_Runtime\user\mods\CustomMapLoader\`. Pass `-p:SptRoot=...` for another install. The build also writes `CustomMapLoader.zip`, the mod's release for players.

2. Open `Unity/TushonkaMappingSDK` in Unity 2022.3.43f1. On load, the SDK applies Tarkov's layers, tags, collision matrix and navmesh agent types.

3. Choose **Tarkov SDK > Map > New Map...** and enter `sdk_example`. This creates `Assets/Maps/sdk_example/` with a scene, a starter layout, a baked navmesh, and a Map Definition asset. The layout has ground, cover blocks (one with separate low and high poly colliders), a shed with a door, a switch that opens it, an indoor trigger and a loot spawn, a bot zone, two spawn points, a patrol route, an exfil, the simple time of day, and the game's ambient audio system (add your clips in **Ambient Sounds**).

4. Select the Map Definition, tick **Deploy To Game**, check **Spt Root**, and press **Build Map**.

5. Start the SPT server and the game. **SDK Example** appears on the map select screen.

`Server/maps/sdk_example/base.json` already matches this map id. For a new map, copy that folder, rename it to the new id, and set `Id` and a new unique 24-character hex `_Id` in `base.json`.

Keep at least one entry in `AirdropParameters`, even with airdrops off: the client reads entry 0 at raid start and fails to load the raid on an empty list. The example's entry has `PlaneAirdropChance` and `PlaneAirdropMax` at 0. If the list is empty, the server mod adds that entry itself.

## How a map loads

```
base.json  Scene = { path: "maps/<id>.bundle", rcid: "<id>.scenespreset.asset" }

client plugin  BepInEx/plugins/CustomMapLoader/bundles/maps/<id>.bundle         EFT.ScenesPreset
                                                         maps/<id>_scenes.bundle  every scene of the map
```

1. The server mod adds the location to SPT's location table, merges the SDK's `sdk_export.json` (spawn points, exits, export time) into `base.json`, and adds the map name to the locales.
2. When a raid starts, the game asks `BundlesManager` for `maps/<id>.bundle`. The client plugin loads the file from its own folder and inserts it into the loaded-bundle cache, so nothing is copied into `EscapeFromTarkov_Data`.
3. The game reads the `ScenesPreset`, loads the first scene as Single and the rest as Additive, and activates the first.
4. When bots initialise, `AICoversData.RestoreData` normally restores baked AI data. On an SDK map that data is empty, so the plugin builds it from the navmesh: voxel grid, cover points and ways, core points, door links, container and exfil AI points, and patrol routes for zones without authored ones.

## Authoring maps

### Layers

The game finds geometry by layer. Solid colliders on `Default` are ignored by bullets and bots.

| Purpose | Layer |
| --- | --- |
| Simple static geometry (players, bullets and AI sight) | `Terrain` |
| Player movement only | `LowPolyCollider` |
| Bullets and AI sight only | `HighPolyCollider` |
| Spawn points, exfils, other triggers | `Triggers` |

Add a `BallisticCollider` to every collider on `Terrain` or `HighPolyCollider`; the game reads surface material and penetration from it. **Tarkov SDK > Collision** sets layers and adds these components for the selection.

### Components

All game components are under `Assets/TarkovSDK/GameStubs`. They contain only the game's serialized fields; the game supplies the behaviour. **GameObject > Tarkov SDK** places pre-configured ones.

- **LocationScene**: one per scene. The builder fills its arrays; the game reads them instead of searching the scene.
- **LevelSettings**: one in the first scene. It sets Unity's fog, skybox and ambient settings and initialises airdrops.
- **Time of day**: BSG's shaders take ambient light only from a time-of-day system, so without one the map renders black. Pick one in **Tarkov SDK > Map > Time of Day / Weather...**; **Build Map** adds the simple one if the scene has neither.
  - **Simple** (`Weather`): Factory's static rig of `TODSkySimple`, `TOD_Time`, `TodLightSetter`, a directional `Sun` and an `AmbientLight`. In the raid the sun follows the raid's clock: the client places it for the hour at the rig's `Latitude`, facing the LevelSettings north direction, and fades light and fog for night. The `TODSkySimple` inspector has a **Preview Hour** slider. Factory's values are dim, so raise `AmbientBrightness` or the sun intensity for a brighter map.
  - **Full weather system** (`SDK_Weather`): Customs' `TOD_Sky` sky dome, `WeatherController`, clouds, wind, rain and snow, driven by the server's weather and season. Its values come from your own game: the window's **Extract From Game** runs `Tools/WeatherExtract/extract_weather.py` (Python with `pip install UnityPy TypeTreeGeneratorAPI`), which writes `Assets/TarkovSDK/Weather/Extracted/`. That folder and the generated materials are gitignored and never shipped with the SDK. The root is saved inactive; the client swaps its stand-in shaders for the game's and then activates it. It also sets LevelSettings like Customs: no skybox, the TOD sky supplies the ambient light.
  - **Freeze Time** (both modes) keeps the time at the raid's start time for the whole raid.
  - **Rain cover** (full mode): the game shows rain on the screen and puddle ripples only where the camera is under open sky, which it reads from a `WeatherObstacle` of dry planes. **Generate Rain Cover** (in the window, or **Tarkov SDK > Map**) scans the map from above and places dry planes under every roof. Without one, the whole map counts as outdoors.
- **SpawnPointMarker**: set `Sides`, `Categories` and, for PMC player spawns, `Infiltration` (the entry point name). Place it under a BotZone to assign it to that zone.
- **BotZone**: its GameObject name is the zone name used by `waves` and `OpenZones` in `base.json`.
- **PatrolWay** with child **PatrolPoint**s: an authored bot route. Zones without one get a generated route.
- **ExfiltrationPoint**: needs a trigger collider. `Settings.Name` becomes the exit name; `Settings.EntryPoints` lists which infiltrations may use it.
- **Door**, **LootableContainer**, **Switch**, etc. are available. The builder assigns missing ids.

### Indoor areas and transits

- **GameObject > Tarkov SDK > Indoor Trigger Group** adds a group (for example one per building) under the map's root trigger group, or inside the group you right-click. The game tests a group's bounds before its triggers.
- **GameObject > Tarkov SDK > Indoor Trigger** marks an interior (indoor audio, exposure, rain volume). The trigger is its own box: scale the object to cover the room. The SDK creates it under an `IndoorTriggers` TriggerGroup inside the EnvironmentManager, where the game looks for it, and computes its bounds at build. Keep triggers in that group.
- **GameObject > Tarkov SDK > Transit Point** adds a transit to another map: a trigger box with a unique `id`. Set `parameters.location` to the destination's map id (`bigmap`, `Woods`, or a custom map's id); the server mod fills in its `_Id` (`target`). Use **Edit Server Data > Add Missing Locale Keys** to give the transit its displayed name.

### Ambient sounds

**Tarkov SDK > Map > Ambient Sounds...** takes looping clips for outdoor day, outdoor night, wind, rain and indoor room tone, and creates 2D sources under `SDK_Ambience`. In the raid the client routes them into the game's ambience mixers, so they follow its volume and the game crossfades outdoor and indoor sound at indoor triggers. Day and night beds fade with daylight, and the rain bed with the rain. **GameObject > Tarkov SDK > Ambient Sound Point** places a 3D sound (birds, machinery). Name tags on any AudioSource (`[Outdoor]`, `[Indoor]`, `[Nature]`, `[Technical]`, `[Day]`, `[Night]`, `[Rain]`, `[Clear]`) do the same for your own sources.

**GameObject > Tarkov SDK > Random Sound Events (3D / 2D)** uses the game's own ambient player (`SeasonAmbientSoundPlayer`): after a random delay it plays a random clip from a SoundBank (a crow, distant gunfire, a dog), and repeats. Add clips, set the delay range, volume and 3D range in its inspector; the SDK creates the SoundBank under the scene's `SoundBanks` folder and uses it for every season. On retail maps the game's AmbientAudioSystem starts these players; on SDK maps the client routes them into a game mixer and gives them the raid's season.

**Use The Game's Ambient Audio System** (in the same window) replaces the simple beds with the game's own `AmbientAudioSystem`, laid out as on Customs: day and night loops crossfaded at the game's hours (5:15 and 21:15), wind and rain loops chosen by the weather and the player being indoors or outdoors, and random sound event groups (Day, Night, Always, Indoor) that the system starts and stops. Its clips go into a generated `SeasonAmbientSoundData` asset under the scene's `AmbientAudio` folder, used for every season. **Extract Reverb Data From Game** reads Customs' per-room ambient reverb and delay from your own game (`extract_weather.py --mixer-only`, gitignored like the weather data). The SDK can't reference the game's audio mixer, so objects name their mixer group at the end of their name (`DaySource @AmbientOutDayBypass`) and the client assigns it.

SDK maps have no spatial audio (the game's hand-placed audio rooms and portals, plus a baked routes file), so the client stands in for it with the indoor triggers: it crossfades the indoor and outdoor ambience mixers and tells the ambient system whether the player is in an indoor or an outdoor room.

### Doors, switches and collision

- **GameObject > Tarkov SDK > Door** adds a working door laid out like retail ones: the leaf pivots at its hinge on the Interactive layer, with an interaction trigger, a high poly collider (bullets, wood) and a door low poly collider (movement). Replace the placeholder panel with your model and resize the colliders to match.
- **GameObject > Tarkov SDK > Switch** adds a switch. Right-click a door to make a switch that opens it; otherwise set its `Door` or `Lamps`.
- **Tarkov SDK > Collision > Add Low + High Poly Colliders To Selected** splits collision the way retail maps do: movement on LowPolyCollider (box or mesh), bullets and AI sight on HighPolyCollider with a BallisticCollider guessed from the material name.

### Bots, spawns and loot

The **Server Data Editor** (Map Definition > Edit Server Data) has tabs for:

- **Bots**: bot limits and the scav mix, open zones, **waves** (bot type, difficulty, count, time and zones picked from the scene's bot zones) and **boss spawns** (boss, chance, escorts, zones). These are `base.json` values; restart the SPT server after saving.
- **Spawns**: every spawn point in the open scenes, with buttons to add player and bot spawns.
- **Loot**: every loot spawn, with the expected loose loot per raid.

**GameObject > Tarkov SDK > Loot Spawn** places a loose loot spot. Search items by name in its inspector (it reads the SPT install's item database), weight them, and set a chance or **Always Spawn**. Loot spawns are editor-only; **Build Map** exports them and the server builds the map's loose loot from them.

### Keeping bots out of an area

Bots path only on the navmesh, and the client generates their cover points and patrols from it. To block an area, use **GameObject > Tarkov SDK > Nav Blocker**: a translucent red box set to the **Not Walkable** navigation area, with no collider and the **EditorOnly** tag, so it cuts the navmesh but never ships with the map. Scale it over the area, then rebake with **Tarkov SDK > Map > Bake NavMesh For Active Scene**. To block a whole object instead, set its **Navigation Area** to **Not Walkable** in the Navigation window; to make bots avoid an area without forbidding it, use **Danger**.

### Materials and shaders

Pick shaders under **Tarkov/** in the material inspector. Each one is an editor stand-in for the game shader of the same name, without the prefix (`Tarkov/p0/Reflective/Bumped Specular SMap` stands in for `p0/Reflective/Bumped Specular SMap`). It has the game shader's properties and keyword toggles, but only a simple preview. When the map loads in a raid, the client plugin moves every such material onto the game's compiled shader. Textures and values carry over because the property names match.

| Use | Shader |
| --- | --- |
| Most props, buildings and terrain meshes | `p0/Reflective/Bumped Specular SMap` |
| Alpha-tested props (fences, grates, foliage cards) | `p0/Cutout/Bumped Diffuse` |
| Glass | `Global Fog/Transparent Reflective Specular` |
| Vertex-painted ground | `Custom/Vert Paint Shader Solid` |
| Emissive (lamps, screens, signs) | `p0/Reflective/Bumped Emissive Specular SMap` |

Unity's built-in **Standard** shader also works in the game and needs no swap; retail maps use it on some materials. Shaders not under **Tarkov/** are bundled with the map as they are.

The skeleton's ground and cover blocks use `Assets/TarkovSDK/Materials/SDK_Default.mat`.

### Importing models

**Tarkov SDK > Model > Convert Selected Model For Tarkov** turns an imported model (GLB/glTF, FBX, OBJ, such as a Source map exported from Blender) into map geometry. Select its object in the scene, or the model file in the Project window to place it first. It:

- replaces every material with one on a Tarkov stand-in shader, saved in a `Materials` folder next to the model; textures, colours and tiling carry over, and alpha-blended materials become cutout
- adds a MeshCollider, the Terrain layer, static flags and a BallisticCollider to every mesh, with the surface type guessed from the material name (metal, grass, glass, wood ...)
- makes Source tool faces (`toolsskybox`, `toolsclip` ...) invisible walls on the LevelBorder layer, hides `toolsnodraw`/triggers, and leaves decals and water without collision
- tags nodes scaled up for a Source 3D skybox as EditorOnly so they stay out of the build
- rebakes the navmesh

Running it again is safe; converted materials are reused and can be tuned by hand afterwards.

### Building

The **Build Map** button:

1. opens the map's scenes, fills `LocationScene` arrays, syncs spawn points into their bot zones, links patrol points, lowercases exfil entry points, and stamps `LocationExportInfo`;
2. writes `Assets/TarkovSDK/Generated/<id>/<id>.ScenesPreset.asset`;
3. builds `maps/<id>.bundle` and `maps/<id>_scenes.bundle` into `Build/`;
4. writes `Build/maps/<id>/sdk_export.json`;
5. if **Deploy To Game** is ticked, copies the bundles to `BepInEx/plugins/CustomMapLoader/bundles/maps/` and the export to `SPT_Runtime/user/mods/CustomMapLoader/maps/<id>/`. These folders are fixed; the build warns if CustomMapLoader itself isn't installed there.

Restart the SPT server after a build, because it reads `sdk_export.json` at startup. Warnings and errors from the build appear in the Unity console.

### Sharing a map

**Create Package** (on by default) writes `<Output Folder>/<MapId>.zip`: the map bundles, `base.json`, `locale.json`, `sdk_export.json` and a short readme, laid out like an SPT install. It contains no DLLs. Players install CustomMapLoader once, then extract map packages into their SPT folder.

`base.json` and `locale.json` are taken from the SPT install the definition deploys to, so set them up with **Edit Server Data** first.

## Updating the stubs

After a game update, regenerate the stubs from the new `Assembly-CSharp.dll`:

```sh
dotnet run --project Tools/StubGenerator -c Release -- C:/spt41/EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll Tools/StubGenerator/stub-roots.txt Unity/TushonkaMappingSDK/Assets/TarkovSDK/GameStubs
```

`stub-roots.txt` lists the components that get full stubs. Types they reference are added automatically: enums and `[Serializable]` classes in full, other components as empty stubs. `GameStubs/_StubReport.txt` lists every emitted type and every skipped field.

The project-wide tags, layers, collision matrix and navmesh agents are in `Assets/TarkovSDK/Editor/TarkovProjectSettings.cs`, read from the game's `globalgamemanagers`.

## Updating the shader stand-ins

`Tools/ShaderStandins/shaders.txt` lists the game shaders that get stand-ins. To add one or refresh them after a game update (needs Python with `pip install UnityPy`):

```sh
python Tools/ShaderStandins/generate_standins.py C:/spt41/EscapeFromTarkov_Data/StreamingAssets/Windows/shaders Tools/ShaderStandins/shaders.txt Unity/TushonkaMappingSDK/Assets/TarkovSDK/Shaders/Standins
```

Reading the 378 MB shader bundle takes a few minutes.

## Status and limitations

This is an untested prototype. Every project compiles, Unity compiles the SDK, and the server mod's registration was checked offline against SPT 4.1.6's own types. No raid has been played on an SDK map yet.

Not implemented:

- **Loot**: static containers and loose loot are empty. Container loot pools are borrowed from Factory, but there is no export of container ids to `staticContainers.json` yet.
- **Scav raids**: the example map sets `DisabledForScav`, because no scav exfils are authored.
- **PMC bots**: the example `base.json` has no `BossLocationSpawn` entries, so only the scav wave spawns.
- **Loose-loot AI points**: bots won't path to loose items.
- **Lighting and rendering**: the full weather system is new and untested in raids. SDK maps use the game's fallback camera, which has no `TOD_Scattering`, so the full system's atmospheric fog falls back to LevelSettings fog. Two snow shaders (`EFT/Snow Sparkling`, `EFT/SnowCopyNormalAndAlphaMask`) only exist in Customs' scene files, so winter snow wetness renders without them. Lightmaps and occlusion culling are not handled. Deferred decals and tree/foliage shaders (SpeedTree, impostors) have no stand-ins yet.
- **Ballistics tuning**: `BallisticCollider` penetration values are left at the game's defaults.
