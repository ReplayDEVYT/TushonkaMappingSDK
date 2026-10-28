# CustomMapLoader

A mod that loads custom Escape from Tarkov maps in SPT 4.1.6, and the Unity SDK for building them. Documentation is written by AI, but still matches what you should be doing. I've tested all of this, no worries.

```
BepInEx/plugins/CustomMapLoader/bundles/maps/<id>.bundle, <id>_scenes.bundle, <id>_audio/ (baked spatial audio, Meta acoustics)
SPT_Runtime/user/mods/CustomMapLoader/maps/<id>/base.json, locale.json, sdk_export.json
```

**Author:** mizmii
**Version:** 1.8.0
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

3. Choose **Tarkov SDK > Map Settings > New Map...** and enter `sdk_example`. This creates `Assets/Maps/sdk_example/` with a scene, an example layout and a Map Definition asset. The layout is 200 x 200 m of ground with:
   - cover blocks (one with separate low and high poly colliders)
   - a shed with a breachable door, a switch that opens it, an indoor trigger and a loot spawn
   - a concrete bunker whose indoor trigger has **Is Bunker** on, with a volumetric light shaft
   - a bot zone, two spawn points and a patrol route
   - one extract of each kind: regular, scav, co-op, car (a stand-in box car) and flare (with its sniper zone)
   - a minefield and a claymore in the north-west corner
   - airdrop points and the physics setup the plane needs
   - a BTR route that comes in, drives laps of three stops and leaves, on drive-through roads with their turns
   - two pines and a broadleaf tree, the SDK's own
   - a fire barrel, an explosive gas cylinder (shoot it, or wait: a world event warns everyone and sets it off 2 to 4 minutes in), three crates in a container group (one or two spawn), a mounted Utes on its platform and a line of wires on poles
   - a mortar zone with its battery, and a trigger zone beside it: walk in and two scavs spawn, and the mortar strikes 15 s later
   - spatial audio rooms for the shed, the bunker and the outdoors
   - the simple time of day, the game's ambient audio system and a baked navmesh

   Add your ambience clips in **Ambient Sounds**, and pick the indoor and bunker room tones in **Spatial Audio**.

4. Select the Map Definition, tick **Deploy To Game**, check **Spt Root**, and press **Build Map**.

5. Start the SPT server and the game. **SDK Example** appears on the map select screen.

`Server/maps/sdk_example/base.json` already matches this map id. For a new map, open **Edit Server Data** and press **Create From sdk_example Template**: it copies that `base.json` with the new `Id` and a new unique `_Id`. To do it by hand, copy the folder, rename it to the new id, and set `Id` and a new unique 24-character hex `_Id` (the **Generate** button next to `_Id` on the General tab makes one).

Keep at least one entry in `AirdropParameters`, even with airdrops off: the client reads entry 0 at raid start and fails to load the raid on an empty list. The example's entry drops at most one crate a raid (`PlaneAirdropChance` 0.5 per try, `PlaneAirdropMax` 1) and works solo (`MinPlayersCountToSpawnAirdrop` 1; retail's 6 means no plane drops with fewer than 6 players). Set `PlaneAirdropMax` to 0 for no plane drops. If the list is empty, the server mod adds that entry itself.

## Menus

**Tarkov SDK** in the menu bar:

| Menu | What's in it |
| --- | --- |
| **Build Selected Map** | Builds the selected Map Definition. |
| **Generation > Essential** | Map Skeleton In Active Scene, Bake NavMesh, Indoor Triggers, Patrol Routes. |
| **Generation > Interactables** | Interactive Object Builder (doors, lamps, containers, switches, physics props, animated objects from a model). |
| **Generation > Audio** | Spatial Audio (the window), Generate Rooms And Portals, Bake Sound Routes. |
| **Generation > Ambience** | Ambient Sounds. |
| **Generation > Time** | Time of Day / Weather. |
| **Generation > Geometry** | Rain Cover. |
| **Generation > Misc** | Airdrop Points, BTR Roads And Turns, Lens Flare Settings, Post-Processing Preset. |
| **Tools > Extracts** | Extracts (every extract and transit of the scene). |
| **Tools > Interactables** | Loot Containers, Make Selected Doors Breachable. |
| **Tools > Triggers** | Triggers & Sounds. |
| **Tools > World Events** | BTR Path Editor, Mortar Shelling, Scripted Events. |
| **Tools > Geometry** | Convert Selected Model For Tarkov, Make Selected Static Geometry, Add Low + High Poly Colliders, Add BallisticCollider, Refresh Default Surface Material, Terrain Surfaces, New Vertex Paint Material (On Selected), Wire Tool, Bird Path Tool. |
| **Tools > Lighting** | Lighting Editor, Add Volumetric Light To Selected Lights. |
| **Map Settings** | New Map, New Map Pack; **Data > Server Data Editor**. |
| **Decompilation** | Import AssetRipper Export, Clean Up Ripped Scene, Fix Ripped Materials In Selected Folder, Remove Missing Scripts In Selected Folder, Prefab Library, Make Prefabs From Selected, Tree Builder. |
| **Quick Fixes** | Fix Broken Interactables, Fix Night Lighting (Bright or Shiny at Night), Fix Shiny Materials (Missing Reflection Cubemaps), Convert SMap Materials To Bumped Specular, Fix Audio Sources (Meta XR), Apply Tarkov Tags, Layers and Physics, Set Up ProBuilder For Tarkov. |
| **Install Packages** | Meta XR Audio SDK (71.0.0), ProBuilder (5.2.4), Polybrush (1.2.1), Terrain Tools (5.0.6). |
| **Help** | How-tos by topic, troubleshooting, and every message Build Map logs with what it means and how to fix it. |

**GameObject > Tarkov SDK** (also the Hierarchy's right-click menu) places objects:

| Submenu | What's in it |
| --- | --- |
| **Spawns**, **Exfils**, **AI** | Spawn points and loot spawns; every kind of extract, transits and Forced Transit; bot zones, patrols, nav blockers. |
| **Interactables** | Door, Breachable Door, Keycard Door, Switch, Lootable Container, Container Group, Automatic Sliding Door, Breakable Window, Physics Prop, Animated Object, Mounted Gun (Utes). |
| **Hazards** | Traps (barbed wire, spike trap, flame trap, trap door, trap switch), Sniper Zone, Flare Zone, Minefield, Claymore, Fire, Fire Barrel, Explosive Barrel. |
| **Lights**, **Sound** | Lamps, area lights, flares, lamp groups, Volumetric Light Shaft; ambient points, random sounds, sound zones, the PA. |
| **Environment**, **Quests** | Indoor triggers, fog, terrain, trees, Wire, Birds (Flight Paths), Bird Flock; quest zones. |
| **World Events** | What happens on the map by itself: Airdrop Point, BTR Route, Mortar Zone and Battery, Scripted Event. |
| **Triggers** | What fires a trigger id (Shootable Trigger), then what a trigger id does: Explosion, Notification, Caption, Health Change, Effect, Gear Strip, Spawn Bots, Spawn Loot, Spawn Prefab. |

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
5. Scene-level managers retail maps carry and SDK maps can't author are added when the first player spawns: the culling managers (Fika's other players animate their arms and weapons only while one says they are visible), the environment manager and the stationary weapon controller.
6. With SAIN installed, the plugin tells it which retail map a custom map counts as (config **Compatibility > SAIN map type**, default Customs). SAIN only knows retail maps; on others it logs an error every frame, enough to make the game stutter.

## Authoring maps

### Layers

The game finds geometry by layer. Solid colliders on `Default` are ignored by bullets and bots.

| Purpose | Layer |
| --- | --- |
| Simple static geometry (players, bullets and AI sight) | `Terrain` |
| Player movement only | `LowPolyCollider` |
| Bullets and AI sight only | `HighPolyCollider` |
| Spawn points, exfils, other triggers | `Triggers` |

Add a `BallisticCollider` to every collider on `Terrain` or `HighPolyCollider`; the game reads surface material and penetration from it. **Tarkov SDK > Tools > Geometry** sets layers and adds these components for the selection.

### Components

All game components are under `Assets/TarkovSDK/GameStubs`. They contain only the game's serialized fields; the game supplies the behaviour. **GameObject > Tarkov SDK** places pre-configured ones.

- **LocationScene**: one per scene. The builder fills its arrays; the game reads them instead of searching the scene.
- **LevelSettings**: one in the first scene. It sets Unity's fog, skybox and ambient settings and initialises airdrops.
- **Time of day**: BSG's shaders take ambient light only from a time-of-day system, so without one the map renders black. Pick one in **Tarkov SDK > Generation > Time > Time of Day / Weather...**; **Build Map** adds the simple one if the scene has neither.
  - **Simple** (`Weather`): Factory's static rig of `TODSkySimple`, `TOD_Time`, `TodLightSetter`, a directional `Sun` and an `AmbientLight`. In the raid the sun follows the raid's clock: the client places it for the hour at the rig's `Latitude`, facing the LevelSettings north direction, and fades light and fog for night. The `TODSkySimple` inspector has a **Preview Hour** slider. Factory's values are dim, so raise `AmbientBrightness` or the sun intensity for a brighter map.
  - **Full weather system** (`SDK_Weather`): Customs' `TOD_Sky` sky dome, `WeatherController`, clouds, wind, rain and snow, driven by the server's weather and season. Its values come from your own game: the window's **Extract From Game** runs `Tools/WeatherExtract/extract_weather.py` (Python with `pip install UnityPy TypeTreeGeneratorAPI`), which writes `Assets/TarkovSDK/Weather/Extracted/`. That folder and the generated materials are gitignored and never shipped with the SDK. The root is saved inactive; the client swaps its stand-in shaders for the game's and then activates it. It also sets LevelSettings like Customs: no skybox, the TOD sky supplies the ambient light.
  - **Freeze Time** (both modes) keeps the time at the raid's start time for the whole raid.
  - **Fog** (full mode): the world fog follows the raid's weather, from the map's own density up to thick fog. The game's volumetric (MBOIT) weather fog only exists on Shoreline and Labyrinth: it needs a renderer whose shaders ship only in Shoreline's scene files, so SDK maps, like Customs or Woods, don't have it. For local volumetric-looking fog, place **Fog Volumes**.
  - **Rain cover** (full mode): the game shows rain on the screen and puddle ripples only where the camera is under open sky, which it reads from a `WeatherObstacle` of dry planes. **Generate Rain Cover** (in the window, or **Tarkov SDK > Generation > Geometry > Rain Cover**) scans the map from above and places dry planes under every roof. Without one, the whole map counts as outdoors.
- **SpawnPointMarker**: set `Sides`, `Categories` and, for PMC player spawns, `Infiltration` (the entry point name). Place it under a BotZone to assign it to that zone.
- **BotZone**: its GameObject name is the zone name used by `waves` and `OpenZones` in `base.json`.
- **PatrolWay** with child **PatrolPoint**s: an authored bot route. Zones without one get a generated route in the raid; **Tarkov SDK > Generation > Essential > Patrol Routes** makes those routes in the editor instead, so you can see and adjust them: for every zone without a hand-made route, a `GeneratedPatrol` of 2-6 points 7-45 m apart around its spawn points, each reachable on the navmesh from the last. Running it again replaces the generated routes and leaves hand-made ones alone.
- **ExfiltrationPoint**: needs a trigger collider. `Settings.Name` becomes the exit name; `Settings.EntryPoints` lists which infiltrations may use it.
- **Door**, **LootableContainer**, **Switch**, etc. are available. The builder assigns missing ids.

### Maps in several scenes

A map can be split into scenes, for example one per area, one for a big building and one for the sound. The raid loads all of them together. List them in the Map Definition's **Scenes**, with the main scene first. Under **Scenes**, the Map Definition has:

- **Open All Scenes**: opens the map's scenes together, with the first one active. New objects go into the active scene; to work in another scene, right-click its name in the Hierarchy and choose **Set Active Scene**.
- **New Scene** / **Add**: makes a scene next to the first one, with its `LocationScene`, and adds it to the map.
- **Fix Scene Setup**: does what Build Map does below, on the open scenes, with Undo.
- **Add To Map**: shown for a scene that is open and looks like part of the map but isn't in **Scenes**. Build Map warns about such scenes, since they aren't built.

The first scene holds what a map has only once: `LevelSettings`, the time of day or weather, the `EnvironmentManager`, `AIStationaryController` and the ambient audio system. It also holds every indoor trigger, because the game only checks indoor triggers under the first scene's `EnvironmentManager`. **Build Map** moves these into the first scene from the others. If both scenes have one, it warns you to delete the extra; in the raid a second `EnvironmentManager` switches itself off. Build Map also keeps ids unique across all the scenes: doors, lamps, windows, sync objects, spawn points, exfil names and quest zones. This matters when a scene is a copy of another.

- Unity doesn't save references from one scene to another. Keep a switch and the door it opens in the same scene. Trigger ids work across scenes.
- Bake the navmesh, rain cover, indoor triggers and spatial audio with all the scenes open, so each covers the whole map. Build Map warns when a scene has no navmesh on its ground.

### Indoor maps

Tick **Entirely Indoors** on the Map Definition for a map with no outside, like Labs. **Build Map** then covers the whole map with one indoor trigger (`SDK_WholeMapIndoor`: indoor sound, exposure, rain only heard muffled), gives the simple time of day Labs' indoor lighting (`TODSkySimple._forceIndoor` 1, `TodLightSetter._forceIndoorIntensity` 0.5), and with the full weather system puts a rain cover over everything, so rain never falls on the screen. Unticking it removes the trigger and restores the sky's outdoor values on the next build.

### Indoor areas and transits

- **GameObject > Tarkov SDK > Environment > Indoor Trigger Group** adds a group (for example one per building) under the map's root trigger group, or inside the group you right-click. The game tests a group's bounds before its triggers.
- **Tarkov SDK > Generation > Essential > Indoor Triggers** (also in the **Ambient Sounds** window) places indoor triggers for the whole map. It scans from above like the rain cover, on every storey, and treats a space as a room when it has a ceiling (seen from inside, so the ground over a basement never counts) with head room and walls on most sides; carports, overhangs and bridges stay outdoors. Each room is covered with one trigger box, or a few for an L-shaped room, with one trigger group per building, under `GeneratedIndoorTriggers`. Only real geometry counts: objects tagged **EditorOnly** (or under one) and colliders without a mesh, like invisible blockers, are ignored. Regenerating replaces only that group; hand-placed triggers are kept, and areas they cover are skipped. Check the result in the Scene view and fix odd boxes by hand.
- **GameObject > Tarkov SDK > Environment > Indoor Trigger** marks an interior (indoor audio, exposure, rain volume). The trigger is its own box: scale the object to cover the room. The SDK creates it under an `IndoorTriggers` TriggerGroup inside the EnvironmentManager, where the game looks for it, and computes its bounds at build. Keep triggers in that group.
- **GameObject > Tarkov SDK > Exfils > Transit Point** adds a transit to another map: a trigger box with a unique `id`. Set `parameters.location` to the destination's map id (`bigmap`, `Woods`, or a custom map's id); the server mod fills in its `_Id` (`target`). Use **Edit Server Data > Add Missing Locale Keys** to give the transit its displayed name. The **Extracts** window (below) picks the destination from a list.
- **GameObject > Tarkov SDK > Exfils > Forced Transit** adds a transit that sends the player on at once, without the transit menu, its confirmation or its countdown (see [Raid events](#raid-events-forced-transits-gear-stripping-notifications-captions-health-and-effects)).

### Extracts

**Tarkov SDK > Tools > Extracts > Extracts...** lists the scene's extracts and transits, makes new ones and edits the selected one. Pick a **Kind** and **Create At Scene View Centre**:

- **Regular**: walk in and wait the exfil time.
- **Scav**: for scav raids only.
- **Co-op (PMC + Scav)**: a PMC and a scav extract together, on an 8 s shared timer.
- **Car (V-Ex)**: pay 5000 roubles; up to 4 players leave together after 60 s, and the car drives off. It's there on half the raids, and gone on the others. Give it your car model: its four wheels must be objects with "wheel" in their name. Without a model you get a stand-in box. The idle and drive-off sounds are optional. The car is a physics vehicle (the game's `WheelDrive`), so it settles on the ground at raid start; place it on a flat spot. Move the points under its **Drive Path** to set where it drives when it leaves.
- **Flare**: an extract behind a sniper zone, with a flare zone (green) in front. Firing a green flare (the green 26x75mm cartridge or signal flare) in the flare zone makes the sniper hold fire for that player and their group.
- **Elevator**: the Labs elevators (see Facility systems).

Tick **As Transit** to make it a transit instead. The selected extract's **Make It A Transit** button converts it later, and a transit's **Make It An Extract** converts it back. A car extract made a transit keeps its car parked; a flare extract keeps its sniper and flare zones. With **As Transit**, **Forced** makes it a forced transit (no transit menu, confirmation or countdown; see [Raid events](#raid-events-forced-transits-gear-stripping-notifications-captions-health-and-effects)). A selected transit has the same **Forced** toggle, with **Hidden (Trigger Only)**, **On Enter**, **Trigger Id**, **Ignore Requirements** and **Connect A Zone Or Switch...**, which opens Triggers & Sounds set to force it.

For the selected extract the window edits:
- name (also the locale key of its in-raid name), chance, exfil time, timer type and max players
- its **entry points**: ticks for every PMC spawn's entry point
- its **requirement**: pay an item, carry an item, an empty slot, a world event (switch or power), scav cooperation, or climbing gear

These are the server's exit rules: Build Map writes them to `sdk_export.json`, and the game reads them by the extract's name. For a transit, the window edits its destination map (retail maps and the custom maps installed in SPT are listed), when it opens and how long it takes.

The car, flare, scav and co-op kinds are also under **GameObject > Tarkov SDK > Exfils**.

### Ambient sounds

**Tarkov SDK > Generation > Ambience > Ambient Sounds...** takes looping clips for outdoor day, outdoor night, wind, rain and indoor room tone, and creates 2D sources under `SDK_Ambience`. In the raid the client routes them into the game's ambience mixers, so they follow its volume and the game crossfades outdoor and indoor sound at indoor triggers. Day and night beds fade with daylight, and the rain bed with the rain. **GameObject > Tarkov SDK > Sound > Ambient Sound Point** places a 3D sound (birds, machinery). Name tags on any AudioSource (`[Outdoor]`, `[Indoor]`, `[Nature]`, `[Technical]`, `[Day]`, `[Night]`, `[Rain]`, `[Clear]`) do the same for your own sources.

**GameObject > Tarkov SDK > Sound > Random Sound Events (3D / 2D)** uses the game's own ambient player (`SeasonAmbientSoundPlayer`): after a random delay it plays a random clip from a SoundBank (a crow, distant gunfire, a dog), and repeats. Add clips, set the delay range, volume and 3D range in its inspector; the SDK creates the SoundBank under the scene's `SoundBanks` folder and uses it for every season. On retail maps the game's AmbientAudioSystem starts these players; on SDK maps the client routes them into a game mixer and gives them the raid's season.

**Use The Game's Ambient Audio System** (in the same window) replaces the simple beds with the game's own `AmbientAudioSystem`, laid out as on Customs: day and night loops crossfaded at the game's hours (5:15 and 21:15), wind and rain loops chosen by the weather and the player being indoors or outdoors, and random sound event groups (Day, Night, Always, Indoor) that the system starts and stops. Its clips go into a generated `SeasonAmbientSoundData` asset under the scene's `AmbientAudio` folder, used for every season. **Extract Reverb Data From Game** reads Customs' per-room ambient reverb and delay from your own game (`extract_weather.py --mixer-only`, gitignored like the weather data). The SDK can't reference the game's audio mixer, so objects name their mixer group at the end of their name (`DaySource @AmbientOutDayBypass`) and the client assigns it.

A map with **spatial audio** (below) and the game's ambient audio system sounds like a retail map: the game itself fades the outdoor ambience down inside rooms, lets it in through open doors and windows, and plays the rain and wind for indoors or outdoors. A map without spatial audio gets a stand-in from the client: it crossfades the indoor and outdoor ambience mixers at the indoor triggers and tells the ambient system whether the player is indoors or outdoors.

### Spatial audio

The game's own audio system: the map is divided into **rooms** joined by **portals** (doors, windows, doorways). A sound in another room is heard the way it travels, room to room through the portals, quieter and duller through closed doors and walls; the outdoor ambience comes in through the openings to the outside; and each room has the reverb and echo of its **type**. It is what retail maps ship in their sound scenes, and it works the same on SDK maps.

**Tarkov SDK > Generation > Audio > Spatial Audio...** does it all for the active scene:

- **Create And Generate Rooms** / **Generate Rooms And Portals** scans the map like the indoor triggers. Spaces with a ceiling and walls become rooms; walls and doors split them, so each door ends up between two rooms. Stair steps join into a stairwell. One outdoor room covers the map around them (where rooms overlap, the smallest one counts). Portals are made for every door (opened and closed with the door), every breakable window (open once it breaks) and every opening without either, between rooms and to the outside, sized by rays across them. Generated rooms and portals live under `SpatialAudioSystem/Rooms/Generated` and `Portals/Generated` and are replaced when generating again; rooms you make by hand elsewhere under `Rooms` are kept, and their floor isn't scanned. Select the system to see every room (blue boxes) and portal (orange doors, blue windows, green openings) in the Scene view.
- A room's **Type** picks its reverb and echo: Indoor, concrete, brick, plasterboard, thin or thick wood or metal, fabric, glass, stairs, basement, isolated, vehicle, or the outdoor types. The room's inspector shows and edits that type's reverb (decay time, room size, reflections...) and echo (delay) from the map's ambient system; every room of the type shares them, so give a room you want to sound different its own type. The inspector also sets the room's **Wall Occlusion** (how much its walls muffle sound through them), how loud the outdoor ambience and the rain are inside it, and a **Room Tone** loop.
- **Bake Sound Routes** (Build Map does it too) writes the routes sound takes between rooms, `<scene>.audiobakedata` in the scene's `Audio` folder, in the game's own format. The settings limit how many portals and how far a sound travels. **Rebake On Build** is **Auto** by default: Build Map only bakes the routes again when rooms, portals or these settings changed; **Always** bakes every build, **Never** keeps the file (bake it by hand after changing rooms; Build Map warns). Build Map also gives rooms and portals their ids, links each door and window portal to the door or window at it (within 2 m), and reports portals that don't join two rooms (the raid would fail to load).
- **Add Indoor/Outdoor Blend** adds the game's `EnvironmentSoundBlendSystem` (Customs' values) to an ambient audio system made before 1.6.0; new ones have it. Once it's there, the window shows its **Fade Up Outdoors** (how long the outdoor ambience takes to come back to full when you leave a room) and **Fade Down Indoors**. The game doesn't fade at all under 0.5 s, so with Customs' 0.3 s the outdoor ambience jumps to full the moment you step out. New blends use 1 s. For an older one, **Fade Smoothly** fixes it, and Build Map warns until you do. In the raid, the switch to outdoors also waits a third of a second and is dropped if you're back in a room by then, so a small gap between generated rooms doesn't make the ambience jump up and fade back.
- **Room acoustics (Meta XR Audio)**: the reverb and early reflections the game computes from the real geometry, with Meta's acoustics, as on retail maps. They need the **Meta XR Audio SDK 71.0.0**, the version the game ships: the SDK project lists it in `Packages/manifest.json` (from Meta's package registry), so Unity installs it when the project opens. If it's missing or another version is installed, **Tarkov SDK > Install Packages > Meta XR Audio SDK (71.0.0)** (or the button in the Spatial Audio window) adds Meta's registry and pins 71.0.0. Only **static, visible meshes that never move** are baked. Mark your map's geometry **Static** in Unity. Left out are:
  - physics props, doors, containers and switches, and anything under a Rigidbody or an Animator (breakable windows stay in, Static or not, so the glass keeps blocking sound)
  - disabled or shadow-only renderers, such as invisible borders
  - meshes under 1 m
  - the finer levels of a LOD group (only the coarsest is baked, which makes the bake faster)

  **Set Up Acoustics** copies the meshes that count into editor-only proxies under `SpatialAudioSystem/AcousticGeometry`, one geometry file per scene root. Each proxy gets an acoustic material from its bullet material (concrete, brick, wood, metal, glass, soil...), or keeps one you put on the original. The proxies never ship; the geometry objects do and load their files in the raid. Terrains carry their own geometry. Set Up also adds the acoustic map. The window shows what gets baked and how much was left out for each reason. A scene set up by an older SDK has the geometry on its scene roots; Set Up or the next bake replaces it. Retail maps ship the same kinds of files: `StreamingAssets/Acoustics` holds 565 `.xrageo` and 11 `.xramap` files next to the `.audiobakedata` routes.

  **Map Quality** sets the spacing of the map's points and the reflections per point. Bake time is mostly points times reflections:
  - **Balanced** (1-10 m, 6 reflections) is Meta's default and is used for new maps.
  - **Retail** (0.5-8 m, 8) is Tarkov's own setting and can take several times longer.
  - **Fast** is 2-12 m with 4 reflections. Computing the map is Meta's bake and can take a long time on a big map, so there are three buttons, and the window says what changed since the last bake:
  - **Bake Changes** rewrites only the geometry whose meshes, models, positions or acoustic materials changed, and computes the map only when anything did.
  - **Quick Bake** does the same with a coarse map (points every 2-16 m, 4 reflections instead of 0.5-8 m and 8) that computes much faster, for listening tests. The next **Bake Changes** replaces it with the full one.
  - **Full Rebake** redoes everything.

  Build Map never runs this bake; it ships the files from the last one. The files go under `Assets/StreamingAssets/Acoustics`, and what each bake was made from in the scene's `Audio/<scene>_AudioBake.json`.

- **Indoor ambience (room tones)**: as on retail maps, each room has a **room tone**, a loop the game plays while you're in that room. Customs uses a quiet residential hum at 0.4 in nearly every room, and its own sound in bunkers. Rooms with the same tone carry it on as you walk between them; a different tone crossfades. The window sets the **Indoor Tone** (most rooms) and the **Bunker Tone**, each with a volume. Changing one changes every room that has it or no tone at all, so rooms you gave their own tone in the inspector keep it. **Outdoor Ambience Indoors** is how loud the outdoor ambience is inside rooms away from their doors and windows; near them it comes in by distance. Retail and new rooms use 0. Rooms generated before 1.6.0 had 0.1, so drag it to 0. With spatial audio, the Ambient Sounds window's **Indoor** bed would play in every room on top of the tones. **Use Indoor Bed As Room Tone** moves its sound and volume onto the rooms and removes it. Ambient Sounds' **Auto-Fill From** button puts a `*residential*` clip in the indoor tone and a `*bunker*` clip in the bunker tone. Generating rooms again keeps the tones.
- **Bunkers** come from the indoor triggers. Tick **Is Bunker** on the IndoorTriggers over a bunker; that is also what gives the game's own bunker muffling. Every audio room with most of its area inside them becomes a bunker room, as on Customs and Reserve. A bunker room is isolated, so sound from rooms it doesn't connect to is cut by the distance it travels. It also gets thick walls (wall occlusion 0.8), no outdoor ambience and the **Bunker Tone**. Unticking Is Bunker turns the room back into a plain room with the indoor tone. The rooms follow the triggers while the Spatial Audio window is open, when rooms are generated and on Build Map. Bunker can't be picked as a room type by hand. The Bunker Tone can be picked before any bunker exists; it's kept in the scene's `Audio/<scene>_AudioBake.json`.
- **Audio sources**: the map's own sounds (ambient sound points, random sound events, lamp sounds, PA speakers, any AudioSource) go through the game's spatializer, Meta's, only when set up for it. A 3D source needs **Spatialize** on and a **Meta XR Audio Source**, which gives it Meta's placement and the room acoustics. Without them Unity plays it with plain panning and no room sound, so it stands out next to the game's own sounds. A 2D source (an ambience bed) needs Spatialize off; with it on, Meta plays it as a point at the object. The window counts the sources that aren't set up. **Fix Audio Sources** sets them all up, and **Select Them** selects them. Build Map warns while any are left. The SDK's own sound objects are set up when they're made. A source's **Reverb Send Db** on its Meta XR Audio Source sets how much room sound it gets.

The baked files ship next to the map's bundles, in `BepInEx/plugins/CustomMapLoader/bundles/maps/<id>_audio/`; Build Map deploys and packages them, and the client points the game at them. Without spatial audio a map still loads: sounds play unoccluded, as before.

### Doors, switches and collision

- **GameObject > Tarkov SDK > Interactables > Door** adds a working door laid out like retail ones: the leaf pivots at its hinge on the Interactive layer, with an interaction trigger, a high poly collider (bullets, wood) and a door low poly collider (movement). Replace the placeholder panel with your model and resize the colliders to match.
- **GameObject > Tarkov SDK > Interactables > Lootable Container** adds a searchable wooden crate on the Interactive layer, with high and low poly colliders and a placeholder model to replace. `Template` sets the container type (an item template id; it starts as the Wooden crate). **Build Map** exports every container and the server fills them each raid with the loot retail maps put in that container type (types no retail map uses open empty). **Spawn Type** `Can Be Empty` or `Always Has Loot` always spawns it; `May Not Spawn` spawns it at **Spawn Chance** percent, and a container that didn't spawn can only be opened and closed that raid (list objects in **Game Objects To Destroy** to hide them then).
- **Tarkov SDK > Generation > Interactables > Interactive Object Builder...** makes a working **Door**, **Lamp**, **Lootable Container**, **Switch**, **Physics Prop** or **Animated Object** from your own model: pick the model (a model or prefab asset, or a scene object), an optional material override, the bullet material and impact sound, and the sounds. The object is fitted to the model's bounds (door width and thickness, collider sizes, light position) and can also be saved as a prefab. Doors take a hinge side, swing direction and an optional key (the door then starts locked); containers a container type; switches a door and a lamp to operate; animated objects a **Pivot** (left or right edge for a door, bottom centre, centre or the model's own), **Move By** / **Rotate By** and **Seconds** or your own clips, open and close sounds, and whether they get their **Own Switch** (see Animated Object below). Lamps take the light's type, colour, intensity and range, night-only, a lens flare, turn on/off/hum/flicker sounds, and an **On / Off Look**:
  - **Swap Materials**: two copies of the model with an On and an Off material; the lamp shows one or the other. Works with any shader.
  - **Fade Emission**: the lamp fades `_EmissionVisibility`, with flicker. Needs the game's emissive shader, `Tarkov/p0/Reflective/Bumped Emissive Specular SMap`.
  - **Emission Color**: the lamp sets a colour property (e.g. `_EmissionColor`) to an On or Off colour.
- **Tarkov SDK > Quick Fixes > Fix Broken Interactables** checks every door, switch, lootable container and lamp in the open scenes and repairs what would break in the raid: empty animation curves (a switch without one does nothing when used), objects off the Interactive layer, empty or duplicate ids, deleted entries in lamp and switch lists, light culling that lost its light, trigger zones off the Triggers layer, sounds with pitch 0 (silent in the game), lootable containers whose closed/open positions are unset (opening one moved it to the origin) and container interaction boxes flush with their HighPoly colliders (the interact prompt flickered). Build Map fixes the last two by itself. What it can't decide, like a container without a type or an object without a collider, is listed in the Console.
- **Tarkov SDK > Quick Fixes > Fix Night Lighting (Bright or Shiny at Night)** fixes maps whose surfaces (specular maps most of all) and the player's weapon look shiny or almost fullbright at night. The cause is Unity's own ambient light, set in **LevelSettings > Ambient**: the game adds it to every surface day and night, unshadowed. Retail maps with the full weather (Customs) keep it **Flat**, black, intensity 0 and get all their ambient light from the time of day. The fix sets the open scenes' LevelSettings that way (maps with the simple lighting rig keep a little, Factory's Sky Color 0.03 at intensity 0.6), sets Direction Light Shadow For Unshadowed Shaders to 0 on full weather maps as Customs, matches the scene's Lighting window ambient to it, and removes Unity's default reflection. Baked reflection probes, light probe groups and baked lighting data also stay the same day and night; it lists them for you to remove. Build the map again afterwards. Build Map warns about such ambient settings.
- **Tarkov SDK > Quick Fixes > Fix Shiny Materials (Missing Reflection Cubemaps)**: the game's p0 shaders add a reflection from the material's **Reflection Cubemap** that no light, shadow or time of day changes. Retail materials always have one (dark, blurred environments) with a Reflection Color around 0.23. A material without one reflects a flat grey instead, so the map shines day and night and looks far too bright at night. The fix gives every p0 material in the open scenes (or the materials selected in the Project window) that has none the SDK's reflection cubemap (`Materials/SDK_ReflectionCube.cubemap`, a neutral environment as bright as the game's own), and lowers a Reflection Color above 0.4 to 0.23. Convert Selected Model For Tarkov sets both on new materials, and Build Map warns about materials without one. Materials inside a model file are skipped until extracted.
- **Tarkov SDK > Quick Fixes > Convert SMap Materials To Bumped Specular** switches materials from **p0/Reflective/Bumped Specular SMap** to **p0/Bumped Specular**, which has no fixed cubemap reflection (the SMap shader's reflection doesn't follow the time of day, so it shines at night). It converts the materials selected in the Project window, or with none selected every such material in the open scenes. Colour, textures, specular colour, normal map tiling and the specular values carry over (Glossness becomes Shininess, Specularness becomes Specular Power); the gloss map, cubemap, tint and heat glow are dropped. Materials inside a model file are skipped until extracted in the model's Materials tab. Undo restores them.
- **GameObject > Tarkov SDK > Interactables > Breakable Window** adds a 1.2 x 1 m glass pane that shatters when shot, set up like retail windows: mesh collider on the TransparentCollider layer, glass ballistics, and the game's own crack patterns (shipped in `Assets/TarkovSDK/Windows`). To use your own glass, add a `WindowBreaker` to a glass mesh with a `MeshCollider` and a `BallisticCollider` (Glass) on the TransparentCollider layer: **Build Map** computes the geometry the game needs from the mesh, fills its references and gives it an id. The glass must be thin along one axis and have UVs.
- **GameObject > Tarkov SDK > Interactables > Physics Prop** adds a small object that players push by walking into it, bullets knock over and grenades throw, with the game's impact sounds (Arena's `SmallPhysicsObject`; no Tarkov map uses them). Its **Sound Type** picks the sounds: medium generic, small box, small paper, small plastic, hard plastic, cup, cooler bottle, glass bottle, empty tin can, metal candlestick or monitor. It lands on Terrain, HighPolyCollider and Default geometry, like dropped loot, and settles where placed at raid start. Its push strengths are set from its mass so every prop reacts alike (3 m/s when shot, 1.5 m/s when walked into, 12 m/s at 1 m from a grenade); the Object Builder takes them as speeds. Build one from your own model, with an optional broken version shown once it's disturbed, in the **Interactive Object Builder**.
- **GameObject > Tarkov SDK > Interactables > Switch** adds a switch. Right-click a door to make a switch that opens it; otherwise set its `Door` or `Lamps`.
- **GameObject > Tarkov SDK > Interactables > Breachable Door** adds a door that splinters when kicked in, as retail's wooden doors do (195 across the retail maps); **Tarkov SDK > Tools > Interactables > Make Selected Doors Breachable** does it to doors you already have. Any shut or locked door with **Can Be Breached** offers **Breach**, and a successful kick swings it open; this adds the game's `BrokenDoor` beside the leaf: a burst of wood chips and dust from the lock, splinters that drop, and broken wood round the lock from then on (`On` objects; `Off` objects are hidden, for a broken model of your own). Players who join later see it broken.
- **GameObject > Tarkov SDK > Interactables > Container Group** puts the selected lootable containers into a group (the game's `LootableContainersGroup`): each raid, between its **Min** and **Max** of them spawn, picked by their **Spawn Chance**, as retail's container groups ("3 of these 8 crates", "one of these two safes"). The rest aren't in that raid. The server mod picks them; a gear strip's stash always spawns.
- **GameObject > Tarkov SDK > Interactables > Mounted Gun (Utes)** marks where the game's own NSV "Utes" heavy machine gun stands: the marker is where the gunner sits (on the ground), facing where it fires (it turns 20 degrees either way), with a crate platform under the tripod, as retail Customs' guns stand on sandbags. The Scene view draws the game's own Utes there, read from your game install's bundles (nothing is copied into the project; **Show The Gun** in the marker's inspector turns it off). In the raid the client places the game's prefab there and the server adds the gun to the loot, as Woods' static weapons. Mounted guns in a ripped retail scene (Utes or AGS-30) work as they are. Bots don't man them.
- **GameObject > Tarkov SDK > Interactables > Keycard Door** adds a Labs-style keycard door: a `KeycardDoor` that starts locked, and a **card reader** beside it on the wall. Players look at the reader and use their keycard on it (the card-swipe animation); a wrong card makes the reader flash red. **Key Id** is the keycard's item id (it starts as the yellow Labs keycard); **Additional Keys** lists other cards that open it, **Open On Unlock** opens it straight away, **Lock On Shut** locks it again whenever it closes. **Denied Beep**, **Granted Beep** and **Unlock Sound** are the reader's sounds, and the reader's `Lock` handle takes the swipe sound (**Down Sound**). Replace the reader's placeholder with your model; its `proxy` box is what players look at, and the sphere in front of it is where they stand. A keycard door must keep at least one reader in **Proxies**: the game throws without one, and **Build Map** reports it.
- **Tarkov SDK > Tools > Geometry > Add Low + High Poly Colliders To Selected** splits collision the way retail maps do: movement on LowPolyCollider (box or mesh), bullets and AI sight on HighPolyCollider with a BallisticCollider guessed from the material name.

### Previewing interactive objects

Doors, switches, containers, lamps and the facility systems below have a **Preview** panel at the bottom of their inspector. Its buttons do what a player or the game would, in the editor, with the object's sounds: **Open**, **Close**, **Kick**, **Lock** and **Unlock** a door; **Press** and **Release** a switch, which then operates its door, lamps, next switches and exfil exactly as in a raid; **Turn On**, **Turn Off**, **Smooth Off**, **Flicker**, **Blast Nearby** and **Break** a lamp. They run ports of the game's own code, so the swing, the timing (a player's hand reaching the door first, a switch's 1.5x faster curve, a container's short delay), a lamp's fade-in, blinking and flicker, and which sounds play when, are what the raid will do.

The map features have previews too:

- **Fire** (the burn zone's or the flickering light's inspector): **Light** plays its flames, smoke and sparks and flickers its light as the game's `FlickeringLights` does; **Light Every Fire** lights them all.
- **Shootable trigger**: **Shoot It** fires its id. An **explosion** (its inspector, or a shot cylinder) shows a stand-in fireball (the game's own effect is in its bundles), its reach, and sets off every shootable trigger the blast reaches, as the raid does, so barrels chain; the cylinder disappears.
- **Breachable door**: **Kick** on the door (or **Kick In The Door** on its BrokenDoor) swings it open, bursts the chips and dust, shows the broken wood and drops the splinters.
- **Container group**: **Roll A Raid** shows the containers one raid would have, picked as the server picks them, and hides the rest.
- **Scripted world event**: **Play From The Start** runs its steps in the Scene view (ids fire and their listeners react, objects switch, sounds play, notifications show), at 1x to 60x speed. Bot, loot and mortar steps are logged; prefab spawns appear.
- **Mounted gun** and **volumetric light**: drawn in the Scene view all the time (see their sections).

A preview only moves objects and switches lights; **Stop & Restore** puts the scene back, as does saving, entering play mode, building a map or reloading scripts. **Fade By Camera Distance** makes sounds fade with the scene camera's distance by the game's rolloff, as a player would hear them.

Selected objects draw what they do in the scene view: a door's or container's swing from shut (red) to open (green), and the kick angle, around its hinge; where a player stands to use it; everything a switch operates, labelled with the operation (door, lamps, next switches, exfil status), and for any object the switches that operate it; a lamp's lights; a trigger zone's box and every handler its trigger reaches; an elevator door's closed and open poses; a PA's speakers and their range.

### Facility systems: automatic doors, elevators, announcements and sound zones

These work like Labs', from the game's own components, so they behave (and stay in step for every player) as Labs does. Add your models and sounds to the placeholders.

- **GameObject > Tarkov SDK > Interactables > Automatic Sliding Door** adds a two-panel glass door that slides open when the first player (or bot) walks into its trigger zone and closes when the last one leaves. Its animation is generated next to the scene (`<scene>_AutoDoors`); after replacing the panels (`Panel_L`, `Panel_R`) with your models, set **Slide Distance** and **Open Time** and press **Rebuild Sliding Animation** in its inspector. **Auto Door Sound** takes the open and close sounds. The panels aren't Navigation Static, so bots path through the doorway.
- **GameObject > Tarkov SDK > Exfils > Elevator Exfil** adds a Labs-style elevator extract: press the **call button**, the doors open (exfil status Awaits Manual Activation), step in and press the **floor button**; the doors close and the extraction counts down. **Elevator Exfil (Needs Power)** adds a power switch the call button waits for; **Elevator Exfil (Opens On Approach)** calls the elevator when a player walks up to its doors. The doors are `ExfiltrationDoor`s: pose them open and press **Store As Open Pose** (and closed for **Store As Closed Pose**) to change how they move. The exit exports as a Manual exfil with a World Event requirement; add its name to the locale with **Add Missing Locale Keys**.
- **GameObject > Tarkov SDK > Sound > PA > PA System** adds a public address system: announcements play one after another on all of its **speakers** at once (**PA > Speaker** adds more; spread them around the map). Speakers play through the game's `CommonSounds` mixer group (the `@CommonSounds` in their names). Don't use Labs' `CommonAmbInEffects`: it is an indoor ambience group the game silences while the player is outdoors, so the PA gets quieter as you walk up to it outside. **PA > Announcement On Interaction**, on a right-clicked switch or door, plays a clip on the PA when it opens (change the state in its **Sounds**). **PA > Announcement Zone** plays one when a player walks into a zone. Put the clips in the announcements' **Sounds**.
- **GameObject > Tarkov SDK > Interactables > Animated Object** adds something that moves when used: a door, gate, hatch or lift platform. It has its own switch (opening it opens the object, closing it closes it), and Triggers & Sounds connects any zone or switch to it with **Open an animated object** / **Close an animated object**. Put your model under its `Moving` child; the closed pose is where `Moving` is now. In its `SDK_Animation` child set **Move By** (metres) and **Rotate By** (degrees) for the open pose and **Seconds**, or give it your own **Open Clip** (and **Close Clip**; without one the opening plays backwards). The animation is made next to the scene (`<scene>_Animations`) when you change a value or press **Rebuild Animation**; **Preview Open** shows it in the Scene view. **Open Sound** / **Close Sound** play as it starts moving. It's built like the sliding door: an Animator with Closed and Open states on an `IsOpened` bool, set by a `HandlerAnimator`, synced by `AnimatorSync`. Players standing on a moving platform aren't carried by it, so a lift needs walls or a short ride.
- **GameObject > Tarkov SDK > Sound > Sound Zone** plays a random clip from its pool when a player enters, at the zone or at **Override Sound Position**; a looping one stops when the last player leaves. **Is Force Stereo** plays it unspatialised, for things heard everywhere in the zone.

#### Connecting things: Tarkov SDK > Tools > Triggers > Triggers & Sounds

The game connects these systems by **trigger ids**: names a sender sends and listeners react to. A trigger zone sends its id when a player enters or leaves; a switch or door sends the ids in its **Triggers Map** when it reaches a state (Open, Shut...). A sound, a lamp handler or an open/close handler reacts to the ids it lists. An object's **Id** is not a trigger id: a sound set to play on a switch's Id never plays.

**Tarkov SDK > Tools > Triggers > Triggers & Sounds** does the wiring: pick **When** (a player enters / leaves a zone, the last player leaves, a switch or door opens / closes, an object is shot) and its zone or object, then **Do** (play a sound, stop a sound, announce on the PA, turn lamps on or off, toggle lamp groups, turn traps off or on, open or close a door or switch, open or close an animated object, play an animation on any Animator, force a transit, strip the player's gear, show a notification, show captions, change the player's health, give the player effects, spawn bots, spawn loot, spawn, show or hide a prefab, shell a mortar zone, set off an explosion, start a scripted world event) and its sounds or targets, and press **Connect**. It adds the trigger zone or Triggers Map entry and the listener with matching ids. A looping sound started by a zone plays once for the first player in and can stop when the last one leaves. Below, it lists every trigger id in the open scenes with what sends and what hears it; red ones never fire. **Build Map** and **Fix Broken Interactables** warn about them too.

For example, a switch that starts a looping alarm and another that stops it: When "a switch or door opens" (the first switch), Do "play a sound" (the alarm, Loop), Connect; then When "a switch or door opens" (the second switch), Do "stop a sound" (the alarm sound just made), Connect.

Preview a zone with **Player Enters** / **Player Leaves** in its inspector, and a switch with **Press**: the connected sounds, lamps and doors react.

**Play an animation** plays any clip on any Animator when the trigger fires: the clip gets its own state on the Animator (a controller is made next to the scene if it has none), reached from any state, and stays on its last frame. Use it for one-way movements (a bridge lowering, a shutter rolling up); for something that opens and closes, use an Animated Object.

**When an object is shot** (or caught in a blast) uses the game's `TriggerBallistic`: its BallisticCollider (or a hit box added round it) fires the trigger id when hit, and explosions that reach it count, so blasts chain (**GameObject > Tarkov SDK > Triggers > Shootable Trigger** adds one to the selected object). **Set off an explosion** adds the game's `HandlerExplosion` (**Triggers > Explosion**): it goes off once, with the gas cylinder blast of retail Interchange by default (10 m, fragments, stun and blindness); its inspector picks the effect, radius, strength and fragments. A shot only fires on the shooter's machine, as in retail. **Start a scripted world event** starts a [world event](#scripted-world-events).

### Raid events: forced transits, gear stripping, notifications, captions, health and effects

These are CustomMapLoader's own; Tarkov has none of them. The client plugin runs them from what **Build Map** exports, so they need the client and server mods of the same version as the SDK (1.8.0). Their settings are on `SDK_...` objects, which never ship in the map bundle. Each can run from a trigger id, which Triggers & Sounds connects (and lists).

- **Forced Transit** (**GameObject > Tarkov SDK > Exfils > Forced Transit**, or **Force a transit** in Triggers & Sounds on an existing transit): a transit with an `SDK_ForceTransit` child. **On Enter** sends a player on as soon as they walk into the transit's zone; a **Trigger Id** sends the player who fires it from anywhere. It's an ordinary transit otherwise (destination, required items, active time): it takes the player to the transit's location like the game's own transit, only without the menu, the confirmation and the group countdown. A player without the transit's required items gets the game's "access not granted" message, unless **Ignore Requirements** is on. **Hidden (Trigger Only)** makes it a transit only its trigger uses: it's exported inactive, so the game turns its zone off and it never shows in the raid's transit list or timers, and the player who fires the trigger is sent to its destination from anywhere. It needs a trigger id; its zone size and transit time don't matter.
- **Gear Strip** (**GameObject > Tarkov SDK > Triggers > Gear Strip**): takes the player's gear **On Raid Start** and/or when its **Trigger Id** fires (**Strip the player's gear** in Triggers & Sounds). **Move To Container** puts it in its stash, a lootable container made with it (an empty **Gear Stash** container type, 10 x 16, so a full kit fits); the stash always spawns, and the player can take the gear back from it. **Lose** removes it for good. Tick what's taken: each equipment slot, what's in the pockets, the secure container's contents (off by default) and the special slots. If what the player holds is taken (or is inside something taken), they put it away first. If something doesn't fit in the stash, it stays on the player and the raid shows an error. **Once** strips each player only the first time. **Notify** shows the player a notification when gear is taken (its message, icon, sound and duration, as for a Notification below; on by default).
- **Notification** (**GameObject > Tarkov SDK > Triggers > Notification**, or **Show a notification** in Triggers & Sounds): shows the player who fires its **Trigger Id** the game's own notification popup, with a **Message**, an **Icon** (the game's: Alert, Quest, Note, Mail, Achievement and the rest), a **Sound** from the game's interface (or None), a **Duration** (Default, Long, or Infinite until closed) and optionally a text colour. **Once** shows it once per raid.
- **Caption** (**GameObject > Tarkov SDK > Triggers > Caption**, or **Show captions** in Triggers & Sounds): shows the player who fires its **Trigger Id** lines of text at the bottom of the screen, in the game's own caption view (the one the Lightkeeper's dialogue uses). Each line has its **Start Seconds** from the trigger and a **Duration**; lines that overlap show together. Rich text works (`<b>Name:</b>`, `<color=#ffcc00>`). A new caption replaces one still showing. To voice them, connect a sound to the same zone. **Once** shows it once per raid.
- **Health Change** (**GameObject > Tarkov SDK > Triggers > Health Change**, or **Change the player's health** in Triggers & Sounds): on its **Trigger Id**, the player's health changes. **Change** is Damage (**Amount** HP from each body part picked; a destroyed head or chest kills, as a hit would), Heal (Amount HP back, destroyed parts stay destroyed), Restore (back to full, destroyed parts too, bleeding stopped), Kill, Energy or Hydration (Amount added; negative takes it away). **Damage Type** is what the death screen names. **Repeat** applies it that many times **Interval** seconds apart (0: until the **Stop Trigger Id** fires or the player dies), for gas or radiation zones: Triggers & Sounds' **Stop When They Leave** fills in the zone's Player Leaves id. **Chance** and **Once** as for the rest.
- **Effect** (**GameObject > Tarkov SDK > Triggers > Effect**, or **Give the player effects** in Triggers & Sounds): on its **Trigger Id**, the player gets the game's health effects, as many as listed: light or heavy bleeding, fracture (arms and legs), pain, contusion, stun, disorientation, sand on the screen, intoxication, lethal toxin, radiation, stamina to zero, fatigue, painkiller, any stimulator's or food's buffs by name (Propital, Adrenaline, Obdolbos and the rest from the game's list), or removing negative effects or bleeding from a body part. Each has the **Body Part**, **Duration**, **Strength** and **Delay** that apply to it.
- **Spawn Bots** (**GameObject > Tarkov SDK > Triggers > Spawn Bots**, or **Spawn bots** in Triggers & Sounds): on its **Trigger Id**, **Count** bots of a **Role** (scav, sniper scav, BEAR or USEC PMC, raider, rogue, cultist, cursed scav, or a boss) and **Difficulty** spawn at its child points (bots take them in turn), as the game spawns any bot: their profiles come from the server and they join the nearest bot zone. **As Group** makes them one group with a leader. **Delay** waits after the trigger; **Max Alive Bots** skips the spawn while the map already has that many (base.json's own bot limit doesn't apply to these). Put the points on the navmesh; Build Map warns about points off it. In Fika co-op the host spawns them, for everyone.
- **Spawn Loot** (**Triggers > Spawn Loot**, or **Spawn loot**): on its **Trigger Id**, items appear loose as if dropped, at the spawner itself (**At This Object**, the default; raise it a little off the ground) or, with that off, at its child points: **All** puts each listed item at the next point, **One Per Point** picks one by weight for each point. Search items by name in its inspector. Weapons come built from their default preset. **Use Gravity** off leaves them hanging where placed. In Fika co-op only the host sees them: the game has no way to send a new loose item to the other players.
- **Spawn Prefab** (**Triggers > Spawn Prefab**, or **Spawn, show or hide a prefab**): on its **Trigger Id**, **Spawn** puts a copy of **Prefab** (a prefab asset or a scene object) at each child point, removed after **Lifetime** seconds if set; **Show** switches a scene object on where it is (keep it switched off in the scene), **Hide** switches it off. A prefab asset isn't in the map's bundle by itself, so Build Map puts a switched-off copy of it under `SDK_SpawnTemplates` on every build. It's for things that look and sound: props, lights, fire, smoke, particles, audio, physics props. Doors, containers, switches, extracts and loot are registered with the raid when it starts, so a copy made later doesn't work (Build Map warns). Every player sees it.
- Previewing a trigger in the editor shows captions as the Scene view's notice at their times and logs what health changes and effects would do.

### Scripted world events

**GameObject > Tarkov SDK > World Events > Scripted Event** (CustomMapLoader's own): a timeline that starts at a random raid time between **Earliest** and **Latest Minute**, on a **Trigger Id**, or whichever comes first, with a **Chance** per raid, and can **Repeat** after a pause. Its **Steps** run in order, each after its **Wait** (seconds):

- **Fire Trigger**: emits a trigger id, so anything connected to it reacts: explosions, mortar zones, bot, loot and prefab spawns, lamps, doors, sounds, notifications.
- **Show** / **Hide**: switches a scene object on or off (a fire, smoke, wreckage, a blocked road).
- **Play Sound** / **Stop Sound**: plays or stops a scene AudioSource (turn its Play On Awake off).
- **Notification**: the game's notification popup, for every player.

**Tarkov SDK > Tools > World Events > Scripted Events...** lists the events with each one's timeline (what every step reaches), and its buttons add a step together with what it fires: an explosion, a mortar strike, bots, loot, a prefab, placed at the event to move where they belong. In co-op every player's game runs the event with the same rolls and times (from a seed all players share, the raid's loot ids); bots, loot and shelling happen on the host only, as when a player fires their triggers.

### Hazards

Traps from Labyrinth's, with its values. Replace the placeholders with your models.

- **GameObject > Tarkov SDK > Hazards > Barbed Wire**: a trigger box with the game's `BarbedWire`. A player in it moves at 15 % speed, can't sprint, and the body parts inside get cut. Size the box to your wire model. Its sound is a sound bank shared by the scene's wires (`SoundBanks/BarbedWire`); add clips in any wire's inspector (the game's own barbed wire sounds aren't loaded on custom maps).
- **Hazards > Spike Trap**: a zone that hurts for every metre walked in it (5 to each calf, 2 to each thigh) with a 25 % chance of intoxication, and stops players going prone, jumping and sprinting, as Labyrinth's spike pits. The damage and effects are on its `TriggerZone` child (`HandlerDamage`, `HandlerEffect`); its spikes are under `Effects`.
- **Hazards > Flame Trap**: a zone that burns every half second while a player is in it (10 to the calves, pelvis, chest and upper arms; Labyrinth's outer flame zone, the inner one does 50). Bots walk through. Put your fire effect under `Effects`.
- **Hazards > Trap Door**: a hole cover. Build the hole under it; with **Random Traps** on, some covers are gone each raid.
- **Hazards > Trap Switch (For Selected Traps)**: select spike or flame traps, then add it: pressing the switch turns them off (their triggers are ignored, their `Effects` hidden and any damage already running stops), pressing it again turns them back on. Any zone or switch can do the same through **Triggers & Sounds** (**Turn traps off** / **Turn traps on**). Barbed wire can't be switched off.
- **Random Per Raid** (in a barbed wire's inspector, or a spike trap's `HandlerDamage`) adds a `TrapSyncable`: with **Random Traps** on, each raid keeps only some of these.
- **Random Traps** is in the **Server Data Editor**'s Access tab (base.json `EventTrapsData`): how many trap doors open and how many wires and spike traps marked Random Per Raid stay, each a range from Min up to one below Max. Off, every trap stays as placed. Labyrinth uses 5-9 trap doors and 10-20 wires.

The map-border hazards come from Customs' and Lighthouse's, with their values:

- **Hazards > Sniper Zone**: a box where a player is shot every 3 s. The first shot hits half the time. In the buffer strip along the zone's +X side (yellow in the Scene view; **Buffer Zone** in the inspector), hits land on the limbs two times in three. Deeper in, every shot hits the chest, and after 3 shots in a row the head. Move the **Sniper Position** children to where the shots should seem to come from; that's where the distant shot is heard. The game's SVD sound and muzzle flash aren't loaded on custom maps, so CustomMapLoader plays a loaded sniper-rifle sound and skips the flash. Bots set to ignore snipers and BTR passengers aren't shot.
  - **Flare Zone** in the inspector (or **Add Flare Zone**) links a flare zone: firing a green flare there spares that player and their group.
  - **Immune Items**: players carrying any of these aren't shot. Tarkov has no such option; CustomMapLoader's client adds it from the zone's `SDK_ImmuneItem <template>` children, which the inspector manages.
- **Hazards > Flare Zone (Sniper Immunity)**: the flare zone on its own, for a sniper zone's **Flare Zone** field.
- **Hazards > Minefield**: a box where stepping deep in sets off a mine: 60 damage to the legs and stomach, then 250 on the next blast 4-7 m further on, with contusion and damage to anyone near. Its edges wobble, so the border isn't a straight line. The +X strip (yellow) is safe.
- **Hazards > Claymore**: a MON-50 with three 12 m tripwires fanned out in front (+Z). It goes off once, firing fragments in a 60 degree cone (orange in the Scene view). Replace the placeholder box with your model.

Fire and explosives, from retail's bonfires and Interchange's gas cylinders:

- **Hazards > Fire**: flames, smoke and sparks, a flickering orange light (the game's `FlickeringLights`) and a burn box with the game's `FlameDamageTrigger`: the body parts inside it burn. Put it in your own brazier, bonfire or wreck. **Fire Barrel** is the same in a generated steel drum with its colliders. The flame and smoke textures are generated, on Unity's legacy particle shaders, which the game always loads; add a fire loop with a Sound Zone.
- **Hazards > Explosive Barrel**: a red gas cylinder that blows up when shot (or caught in another blast), as on Interchange: a shootable trigger, the game's explosion on the same trigger id (10 m blast, fragments, contusion) and a `HandlerGOState` that hides the cylinder. Connect more to its trigger id in Triggers & Sounds (a fire that starts, an alarm).

### Airdrops and the BTR

- **GameObject > Tarkov SDK > World Events > Airdrop Point**: a spot where a supply crate can land. **Tarkov SDK > Generation > Misc > Airdrop Points** scatters them every 20 m on open, flat ground: no roof, not indoors, not water, with room for the crate. Retail maps have hundreds. A red flare calls a crate to the nearest point. Plane drops are set in the **Server Data Editor**'s **Airdrops** tab: chance per try, max crates, and when the tries happen. **Min Players** counts living human players only; retail's 6 means no plane drops in a solo SPT raid, so keep it at 1. The plane flies within the map's physics bounds (`LevelPhysicsSettings`); Build Map adds `SDK_PhysicsSetup` around the map's geometry for that. A `LevelPhysicsSettings` you place yourself is kept as it is.
- **GameObject > Tarkov SDK > World Events > BTR Route**: the BTR, as on Woods and Streets. Bake the navmesh first. A new route comes in at `p1`, drives laps of `p2` > `p3` > `p4` and leaves to `p5`. **Tarkov SDK > Tools > World Events > BTR Path Editor** (or **Open BTR Path Editor** in the route's inspector) edits all of it; the inspector keeps the basics:
  - **Stops & Routes**: the BTR's chance, when it arrives, speed, how long it waits at stops and its paint; the **stops** (where it pauses, and where the taxi can take a player; name them for the taxi menu), added at the Scene view's centre or with **Place Stops By Clicking**; and the **routes**: the stops in order, where it comes from and leaves to (any stop, usually an off-road one), laps or once through. One active route is picked each raid. The taxi offers only the stops in **Stops In Order**, so keep **Comes From** and **Leaves To** out of that list; list each stop once. A looping route needs at least three stops: with two it drives the same road back and forth and turns round at every stop.
  - **Roads**: the BTR drives a road between any two stops of its route, because the taxi can send it anywhere on it. **Drive-through roads** (on by default) first turn every route stop to face the way the BTR drives through it (the blue arrows), then make each road leave and enter its stops along those arrows, with a straight run of 8 m at each end, along the navmesh between them, kept 2.5 m off the navmesh's edges. A BTR that arrives facing its next road drives straight on. Off, a road is the plain shortest navmesh path, and the BTR turns round wherever its next road starts behind it. **Generate Missing** makes the roads a route needs; **Regenerate All** remakes them (after moving stops). The navmesh follows paths a person can walk, so check that every road stays on drivable ground.
  - **Editing a road**: press **Edit** on it, then click its points (white) in the Scene view and drag them and their handles (yellow). **Insert Point** adds one half way to the next, **Delete Point** removes one (the ends stay at their stops), **Smooth** recomputes the handles from the points, **Snap To Ground** puts the points on the surface, **Keep Off Walls** moves them clear of the navmesh's edges, and **Rebuild** makes the road again from its stops. Roads you edit are marked and kept when the others are regenerated (unless **Also replace roads edited by hand** is on).
  - **Three-point turns** (as on Woods), made with the roads: when the next road starts behind the BTR, it drives forward and to one side, backs up, and drives back to the stop facing the other way. It needs open ground: the BTR's outline, with half a metre to spare, is swept along the turn and tested against the map's geometry, trying either side and a few sizes (Woods' turn, about 22 m ahead and 11 m to one side, and smaller and larger ones). When none stays clear, the least bad one is used and the log names the stop. Select a stop to see its turns (orange) and **Remake Turns**. The BTR leaves its starting stop (**Comes From**) without a turn, so road generation turns that stop to face its road.
  - **Preview**: the BTR drives a route in the Scene view as the game drives it (a port of its own driving code): its heading, its speed in turns and at stops, the turns (orange hull) and sideways slides (red hull: the game keeps the BTR's heading when the road turns 90 degrees or more under it), with a list of what happened where (**Show** frames it). **Check Routes** drives every active route at once and marks the turns, slides and dead ends; Build Map runs the same check and warns about them.

  In the raid, the driver offers a ride to the route's other stops once the BTR has reached its first stop; **cover fire** is offered after paying for a ride, as on Woods and Streets. Tarkov itself breaks the taxi on **Once through** routes (no retail map uses them); CustomMapLoader's client fixes that. The BTR kills a player it runs over (a 100 damage hit to the head); with Fika installed, the host's own player never took that hit (Fika only applies landmine, sniper and AI hits to it), and CustomMapLoader's client fixes that on custom maps. A once-through route whose **Leaves To** is its **Comes From** never leaves its last stop (the game keys a route's parts by stop); Build Map warns.

  Build Map reports missing roads and turns. CustomMapLoader registers the BTR with SPT from the route, copying Woods' wheel and suspension values, and names the stops in the taxi menu.

### Mortar shelling

The game's own mortar strikes (its artillery shelling, as on Woods, Customs, Shoreline and Reserve): the warning siren, the radio detector's alarm, the whistling shells and their explosions are the game's, set up from the map's data. **GameObject > Tarkov SDK > World Events > Mortar Zone** adds the map's `SDK_Mortar` (its settings), a zone and, the first time, a battery; **Tarkov SDK > Tools > World Events > Mortar Shelling** lists and edits them.

- A **zone** is a grid of target points (red in the Scene view, centred on the zone and turned with it): **Points** across and along, **Grid Step** apart. A strike picks **Points Per Shelling** of them, then fires **Rounds** rounds of **Shots Per Round** shells, each landing within **Point Radius** of a picked point, dropped straight down from 300 m onto the map's geometry. A target point with no ground under it (nothing solid between 300 and -100 m) lands its shell at the world origin; Build Map counts them. The retail values are in the tooltips. **Preview** in the window drops a round in the Scene view.
- A **battery** (**World Events > Mortar Battery**) is where the shells fly from: its child objects are the guns. Retail batteries are three guns 20 m apart, a few hundred metres off the map's edge; you hear the shells come from them. Each zone is shelled by the nearest battery. The game reads a battery's second gun unchecked and never fires its last one, so give it three.
- **Planned shelling** (in the settings): after **First Shelling After** seconds, **Zones Per Shelling** random zones that are **Planned** and **Active** are shelled, **Shellings** times a raid, **Pause Between** apart, each after **Warning Time** seconds of siren.
- **Called strikes**: the **blue signal flare** (`66d97834d2985e11480d5c1e`, the game's mortar call) fired upwards strikes where the player who fired it stands, sized and timed like the zone marked **Called Strike Template** (its position doesn't matter), **Called Strike Delay** seconds later.
- A zone's **Trigger Id** (CustomMapLoader's own) shells it when a zone or switch fires it, **Trigger Delay** seconds later (**Shell a mortar zone** in Triggers & Sounds).

CustomMapLoader registers the shelling with SPT (globals `ArtilleryShelling`), so the game creates its shelling controllers for the map. In Fika co-op the host runs it.

### Quest zones

Quests find zones by their **Id**. These do nothing on their own; a quest (from a quest mod or your own) names the Id.

- **GameObject > Tarkov SDK > Quests > Visit Zone**: an `ExperienceTrigger`, for "visit" and "be in zone" conditions. **Experience** above 0 gives that much exploration XP on the first visit.
- **Quests > Place Item Zone**: a `PlaceItemTrigger`, for "leave an item" and "place a marker" conditions. **Beacon Dummy** is the object shown where the item goes (optional).

Size the box to the area. **Copy Id** in the inspector copies the Id for your quest; **Build Map** warns about zones without an Id or with the same Id as another.

### Bots, spawns and loot

The **Server Data Editor** (Map Definition > Edit Server Data) has tabs for:

- **General**: the location `Id` and `_Id` (**Generate** makes a new MongoID and warns if another map already uses it), the raid time in minutes (`EscapeTimeLimit`, optionally separate PvE and co-op times), the map name and description, and locale keys.
- **Map Select**: how the map shows on the map select and loading screens; see [Map select and loading screens](#map-select-and-loading-screens).
- **Raid**: the rest of the raid's settings, each with retail's values in its tooltip:
  - **Location panel**: what the map select panel shows: area, average raid time, player count, average player level (for the difficulty hint) and the warning (normal, no own-side PMCs like Factory, no PMCs at all like Customs).
  - **Groups**: co-op on or off, and the max group count.
  - **Spawning**: how far from other players a player spawn is picked, the minimum distance to an exit, and scav extracts that are off.
  - **Loot amount**: SPT's loose loot multipliers (normal and PvE) and the container multiplier.
  - **Occlusion Culling**.
- **Access**: whether the map is enabled, locked or insured, the PMC level range, scav raids on or off, group sizes, and **required items** (`AccessKeys`, like the Labs keycard), searched by name from the SPT item database or added by template id. The list is written to `AccessKeysPvE` too unless **Same In PvE** is off. Also **Random Traps**.
- **Airdrops**: the scene's airdrop points (generate or select them) and plane drops (`AirdropParameters`): on or off, chance per try, max crates, when the tries happen, the minimum number of human players, and how far apart drops land.
- **Bots**: bot limits and the scav mix, open zones, **waves** (bot type, difficulty, count, time and zones picked from the scene's bot zones) and **boss spawns** (boss, chance, escorts, zones). These are `base.json` values; restart the SPT server after saving.
- **Spawns**: every spawn point in the open scenes, with buttons to add player and bot spawns.
- **Loot**: every loot spawn, with the expected loose loot per raid.
- **JSON**: the whole `base.json` as text, including the other tabs' unsaved changes, for values no tab covers. **Apply** checks the JSON and passes the edits to the other tabs; leaving the tab or pressing **Save** applies them too, and broken JSON is never saved.

**GameObject > Tarkov SDK > Spawns > Loot Spawn** places a loose loot spot. Search items by name in its inspector (it reads the SPT install's item database), weight them, and set a chance or **Always Spawn**. Loot spawns are editor-only; **Build Map** exports them and the server builds the map's loose loot from them.

**Tarkov SDK > Tools > Interactables > Loot Containers** makes **container types**:

- **New Container Type**: a new item with its own **Width** x **Height** grid, name and description (Tarkov has no way to add one; the server mod makes it from a retail container, the Wooden crate unless **Base Template** says otherwise, which sets its sounds and search time). It gets a new template id.
- **Change Retail Container Loot**: keeps a retail container type (its **Template**, e.g. Wooden crate `578f87ad245977356274f2cc`) and changes only what spawns in it on this map.

**Loot** sets what spawns in it: **Empty**, **Copy Retail** (a retail type's loot from every retail map), **Custom** (the **Items** you list, searched by name, picked by weight, between **Min Items** and **Max Items** per container) or **Retail Plus Custom**. **Place In Scene** adds a container of the selected type; **Use On Selected Container** sets selected containers to it (it's their **Template**). Custom types also show in the Interactive Object Builder's container types. Build Map exports the types the map's containers use, and the server registers them at startup (restart the SPT server after a change).

### Map select and loading screens

The Server Data Editor's **Map Select** tab sets everything about the map's place on the map select screen. Its settings are saved to the map's `menu.json`, and its pictures are copied into the map's `images/` folder. Pictures must be **PNG or JPG** files in the project: the game loads them from the SPT server as they are. Restart the SPT server after saving.

- **Map Pack**: the page the map sits on. **Tarkov (no pack)** puts it on the normal map select screen, among the retail maps.
- **Icon X / Icon Y**: where the map's button sits, in thousandths of the page's picture from its bottom-left corner. The preview shows the page: on Tarkov's page, the retail maps (grey) and your other custom maps (blue); on a pack's page, its background and the pack's other maps. Click or drag in it to move the button. Lines show this map's connections.
- **Time Selection**: players pick the raid's time in the bar under the map: now, or 12 hours later. Retail maps all have it except Labs. Off, the raid always starts at the current time.
- **Location Icon**: replaces the white dot on the map's button. Use a PNG with transparency. It is fitted into 31x31 (the size of the game's dot) with its shape kept, so make it square for the full size and leave no empty border around it. When empty, the map uses its pack's icon, or else the game's dot.
- **Preview**: the picture in the location panel when the map is selected, which is otherwise the game's Factory placeholder. It is also the first loading screen banner. Retail ones are 764x460.
- **Loading Screen Banners**: more pictures the loading screen cycles through while the raid loads, each with a title (the map's name if empty) and a description.
- **Connections**: the dotted lines from this map to other locations, like the game's routes between maps. A line only shows when both ends are on the same page. Transits (**GameObject > Tarkov SDK > Exfils > Transit**) are what actually move players between maps.

#### Map packs

A map pack is an extra page of the map select screen with its own background, for maps that aren't in Tarkov or that belong together. Players turn the pages with the **PREVIOUS** and **NEXT** buttons at the far left and right of the bottom button row, which show once at least one pack is installed. Tarkov's own page is always first, and a map without a pack stays on it.

1. **Tarkov SDK > Map Settings > New Map Pack...** asks where to save a pack and selects it. Set its **Pack Id** (lowercase, used as its folder name), the **Display Name** shown under SELECT LOCATION while its page is open, an **Order** among the other packs, and a **Background** picture to replace the Tarkov map. Tarkov's is 800x800, but any shape works: the game fits it into the map area. An optional **Location Icon** replaces the dot on all the pack's maps that have none of their own.
2. On each of its maps, pick the pack in the Server Data Editor's **Map Select** tab and place the map's button on the background.
3. **Build Map** deploys the pack along with the map and puts it in the map's package. **Deploy To SPT** in the pack's inspector deploys it alone, to `SPT_Runtime/user/mods/CustomMapLoader/packs/<Pack Id>/`.

A map whose pack isn't installed shows on Tarkov's page.

### Keeping bots out of an area

Bots path only on the navmesh, and the client generates their cover points and patrols from it. To block an area, use **GameObject > Tarkov SDK > AI > Nav Blocker**: a translucent red box set to the **Not Walkable** navigation area, with no collider and the **EditorOnly** tag, so it cuts the navmesh but never ships with the map. Scale it over the area, then rebake with **Tarkov SDK > Generation > Essential > Bake NavMesh**. To block a whole object instead, set its **Navigation Area** to **Not Walkable** in the Navigation window; to make bots avoid an area without forbidding it, use **Danger**.

### Lights

Tarkov lights its maps with its own light components on top of Unity's. **GameObject > Tarkov SDK > Lights** places them with retail defaults:

- **Lamp** / **Spot Lamp**: a Unity point or spot light with a `CullingLightObject` (fades the light out from 40 to 50 m and drops its shadows beyond 16 m, as retail lamps do) under a `LampController`, which switches, flickers and breaks it. Its glass **Bulb** is what gets shot out; replace it with your model. A Switch's **Lamps** list and the lamp's time-of-day settings turn it on and off. The build assigns lamp ids.
- **Area Light**: the game's `AreaLight`, soft light from a rectangle (`length` x `size`) shining along its +Z up to `depth`, for strip lights, light panels and windows; it starts pointing down. It has no preview in the editor (the gizmo shows its shape), and the client fills in its shader in the raid.
- **Lens Flare**: a `FlareLight`, the glow sprite around a bulb, picked by `_texId` from the game's flare atlas. Flares only draw through the map's flare settings: **Tarkov SDK > Generation > Misc > Lens Flare Settings** adds them as `SDK_Flares`, built from the game's flare atlas and materials that ship in `Assets/TarkovSDK/Lights/Flares` (taken from Factory; every retail map shares them).
- **Advanced Light**: the game's `AdvancedLight`, a shadowless point light. Only one retail map uses it; prefer a Lamp.
- **Controlled Lamp Group**: the game's `ControlledLampGroup`, which switches many lights at once (a hall's ceiling lights, a room's strip lights), with one on and one off sound, and keeps every player's lights in step. It adds two plain lights to replace or add to; right-click an object that already holds lights to make it the group instead. **Fill From Children** in its inspector lists every light, area light, glowing renderer (materials with emission), lamp and flare under it; **Fill Light From** picks another object to fill from. **Initial Light State** is whether it starts on. Only triggers switch a group: **Add Light Switch** makes a switch that turns it on when pressed and off when pressed again, and **Tarkov SDK > Tools > Triggers > Triggers & Sounds** connects zones and other switches to it (**Turn lamps on / off** and **Toggle lamp groups**). Its preview turns it on and off with its sounds.

- **Volumetric Light Shaft**: a soft-shadowed spot light with the game's `VolumetricLight`, for sunlight through a window or a dusty hall's lamps, with retail's values (scattering 0.4 on spot lights, 0.9 on point lights). **Tarkov SDK > Tools > Lighting > Add Volumetric Light To Selected Lights** adds it to lights you have (lamps' too). In the raid it draws with **Volumetric Lighting** on in the game's graphics settings. The Scene view previews the haze with the component's scattering, extinction and Mie G (**Preview In Scene View** in its inspector), but without the light's shadows, so a shaft isn't cut by walls and window frames there as it is in the raid.

Right-click a lamp to add an Area Light, Lens Flare or Advanced Light to it, so the lamp switches it too.

### Fog volumes

**GameObject > Tarkov SDK > Environment > Fog Volume** places a local patch of fog (a foggy room, mist over water, a smoky hall): a box of slow, soft particle puffs on the game's lit, fog-aware particle shader, so it is shaded by the scene's lights and blends into the weather's fog. Its box is the particle system's **Shape** (scale the object, or **Shape > Scale**; the gizmo shows it). For thicker fog raise **Start Color** alpha or **Emission > Rate over Time**; for tint change **Start Color**. Retail maps use fog-sheet billboards instead, but their shader only ships inside retail scenes.

### Birds

Birds use the game's own bird components. The SDK makes its own crow and gull, in `Assets/TarkovSDK/Birds`, because the game has no bird model outside its scenes. Both kinds flap when they fly level or climb, and glide on the way down.

- **GameObject > Tarkov SDK > Environment > Birds (Flight Paths)** adds birds (a `BirdsSpawner`) with two loops over the spot, 25 and 32 m up, flown in opposite directions. **Count** birds share the paths: one on each path, then **Spawn Delay** seconds, then the next round. They fly about 6 m/s.
- **Bird Flock** adds a flock (a `BirdBoidsSpawner`) that circles a point 35 m up. Each bird steers towards its neighbours and away from those too close, within the outer sphere. A bird with too few others near it stops in the air, so keep at least 5 birds and keep the flock tight.
- **Tarkov SDK > Tools > Geometry > Bird Path Tool...** (or **Bird Path Tool...** on the birds) edits the paths. A path is always a closed loop, because the game sends a bird from the path's end back to its start.
  - **Draw A Path** adds points where you click, each **Height** above the ground. **Enter** or **Close Loop** finishes, **Backspace** removes the last point and **Esc** cancels.
  - **Add A Circle Round The Birds** adds a wavy circle of the chosen radius.
  - Drag a point's sphere to move it across and its arrow to move it up or down. Per path, **Ground** sets every point Height above the ground under it, and **Reverse** flips the direction.
  - Red circles mark where a path goes through something solid. Birds fly straight through it; Build Map warns about it too.
- **Preview Flight** (and **Preview Flock** on a flock) flies the birds in the Scene view for two minutes, ported from the game's code. **Stop** ends it.

The New Map example has crows over the middle and a gull flock in the east. To make your own bird, use any prefab with the game's `Bird` component: an Animator with the triggers `flight` and `soar` and a float `speedMult`, plus a `Carina` child that turns to face the flight.

### Wires

**GameObject > Tarkov SDK > Environment > Wire** hangs wires between points, as retail maps' power and phone lines (a plain mesh of sagging tubes, as BSG's wire generator bakes). The points are the `Point` children of its `SDK_Wire` settings, in order; move one and the wires follow. Settings: **Wire Count** side by side, **Spacing**, **Sag Per 10m** (and a random **Sag Variation** per wire), **Radius**, **Sides**, **Loop**, and **Poles**: a wooden pole with a crossarm (and its colliders) under every point. **Tarkov SDK > Tools > Geometry > Wire Tool...** places a new line by clicking in the Scene view, each click a pole top (**Pole Height** above the ground you click) or a point on a wall or roof (**Points On Surfaces**); **Continue** adds points to an existing line. The mesh is saved next to the scene, in `<scene>_Wires`.

Wires sway in the wind: the SDK's wire material is on the game's own swaying wire shader, **p0/Bumped Specular Wires Wind**, which moves each point by the weather's wind, still at the poles and most at mid-span (longer spans swing more; **Sway** scales it). Wires made before this sway once rebuilt: select the line, or build the map. A wire material of your own sways only on that shader.

### Materials and shaders

Pick shaders under **Tarkov/** in the material inspector. Each one is an editor stand-in for the game shader of the same name, without the prefix (`Tarkov/p0/Reflective/Bumped Specular SMap` stands in for `p0/Reflective/Bumped Specular SMap`). It has the game shader's properties and keyword toggles. When the map loads in a raid, the client plugin moves every such material onto the game's compiled shader. Textures and values carry over because the property names match.

The material shaders below, the sky and the glass are decompiled from the game's compiled shaders (`Assets/TarkovSDK/Shaders/Game/`, see `Tools/ShaderStandins/DECOMPILE.md`), so the Scene view shows what the raid shows: the same surface maths, lit the way the raid's deferred lighting and screen ambient light it, with the same sky reflection, the time-of-day ambient, the LevelSettings fog and, on maps with the full weather, the atmospheric fog. For that the SDK project is in **gamma colour space**, like the game; keep it that way or nothing matches. The lighting preview (Scene view, every quarter second) sets the game's shader globals from the scene's time of day rig, LevelSettings and AmbientLight, so changing LevelSettings' ambient, fog and skybox or the preview hour changes the view as it would the raid. What it can't show: the screen-space shadows of the game's own shadow volumes, rain wetting and snow. Shaders still generated as simple previews (rain, snow, the weather particles) sit in `Shaders/Standins/`.

The preview controls (the Time of Day / Weather window and the simple rig's inspector) also have:

- **Preview Lighting** needs the sky on: turning it on also turns on the full weather's **Sky Dome** (or the simple rig) and its sun if they were off, and while they're off the inspector warns, with a **Turn The Sky Dome On** button. Off, the preview has nothing to light the scene with. It only matters in the editor: the game turns the sky on by itself in the raid.
- **Preview Cloudiness** (-1 clear .. 1 overcast) and **Preview Fog** (0 .. 1): the raid weather's values, applied the way `WeatherController` applies them to the sky (haze, contrast, the ambient light, the atmospheric fog's density, never thinner than a clear raid's). Only maps with the full weather system react; the simple rig has no weather.
- **Preview Wind**: trees and wires sway in the Scene view as the raid's wind sways them, with a **Strength** (0 calm .. 1 the strongest; the raid's comes from the weather's wind speed) and **Direction**. Trees blend from their calm sway to their stormy one; wires swing fully in any wind. It works on every map; in the raid the full weather drives it, and on maps with the simple rig the client sets the game's default light breeze.
- **Preview Post-Processing**: the raid camera's post-processing over the Scene view, decompiled from the game's Prism Effects pass: auto exposure to a middle grey (capped, which is what makes raids darker than the raw frame), the black vignette and the filmic tonemap. It uses the map's post-processing preset when it has one, else the retail camera's values.

**Tarkov SDK > Generation > Misc > Post-Processing Preset** makes `<scene>_PostProcessing.asset` next to the scene with the retail camera's values and assigns it to LevelSettings' **Prism Preset Prefab**; in the raid the camera's Prism Effects takes every setting from it (exposure, tonemap, vignette, bloom and lens dirt, ambient obscurance, chromatic aberration, noise, LUTs, depth of field, god rays). Only exposure, vignette and the filmic tonemap are previewed. LevelSettings' **Post Process Profile Prefab** takes a post-processing stack v2 profile for the camera's PostProcessVolume the same way (not previewed).

**Tarkov SDK > Tools > Lighting > Lighting Editor** puts the values the raid lights the map with in one window, next to the preview controls. Every value belongs to one of the scene's own components (the TOD sky's day and night, the weather's time-of-day controller, the simple rig, AmbientLight, LevelSettings), so the raid uses exactly what you set, and it can be undone. At the top it shows, for the preview hour, how bright the ambient light from above, the side and below is, and the sun or moon light. Below that are sliders: sun and moonlight intensity and shadows, the night sky's brightness by moon phase, the ambient light at night and by day (brightness, saturation, contrast, how much comes from the sky above and the horizon, colours added from above and everywhere), sky reflection intensity and Unity's own ambient. The game's time-of-day curves follow the sun's height, so a night slider changes only the night part of its curve and keeps dusk and dawn. **All values** shows the full curves and gradients.

| Use | Shader |
| --- | --- |
| Most props, buildings and terrain meshes | `p0/Reflective/Bumped Specular SMap` |
| Alpha-tested props (fences, grates, foliage cards) | `p0/Cutout/Bumped Diffuse` |
| Glass | `Global Fog/Transparent Reflective Specular` |
| Vertex-painted ground | `Custom/Vert Paint Shader Solid` |
| Emissive (lamps, screens, signs) | `p0/Reflective/Bumped Emissive Specular SMap` |
| Trees (see [Trees](#trees)) | `Nature/SpeedTreeEFT` |

Unity's built-in **Standard** shader also works in the game and needs no swap; retail maps use it on some materials. Shaders not under **Tarkov/** are bundled with the map as they are.

The skeleton's ground and cover blocks use `Assets/TarkovSDK/Materials/SDK_Default.mat`.

### Importing models

**Tarkov SDK > Tools > Geometry > Convert Selected Model For Tarkov** turns an imported model (GLB/glTF, FBX, OBJ, such as a Source map exported from Blender) into map geometry. Select its object in the scene, or the model file in the Project window to place it first. It:

- replaces every material with one on a Tarkov stand-in shader, saved in a `Materials` folder next to the model; textures, colours and tiling carry over, and alpha-blended materials become cutout
- adds a MeshCollider, the Terrain layer, static flags and a BallisticCollider to every mesh, with the surface type guessed from the material name (metal, grass, glass, wood ...)
- makes Source tool faces (`toolsskybox`, `toolsclip` ...) invisible walls on the LevelBorder layer, hides `toolsnodraw`/triggers, and leaves decals and water without collision
- tags nodes scaled up for a Source 3D skybox as EditorOnly so they stay out of the build
- rebakes the navmesh

Running it again is safe; converted materials are reused and can be tuned by hand afterwards.

### Terrain

Unity terrains work as ground, with bullets and footsteps taking each painted layer's surface, as on retail maps (the game's own `TerrainBallistic`: wherever a layer is strongest, that's the surface).

- **GameObject > Tarkov SDK > Environment > Terrain** adds a 256 x 256 m terrain set up for the game, with Grass and Dirt layers to start from (placeholder textures in `<scene>_Terrain`). Sculpt and paint it with Unity's terrain tools; **Tarkov SDK > Install Packages > Terrain Tools (5.0.6)** adds Unity's extra brushes (erosion, noise, smoothing and more).
- **Tarkov SDK > Tools > Geometry > Terrain Surfaces** lists every terrain and its layers. Pick each layer's **Surface**: Soil (dirt, mud, sand), Grass, Gravel (rock, rubble), Asphalt, Concrete or Water (puddles); those are the six the game has for terrain. The surface belongs to the layer asset, so a grass layer is grass on every terrain. Layers you haven't set are guessed from their names and marked "guessed". The terrain's **TerrainBallistic** component shows the same list.
- **Set Up** (or **Set Up All**) puts any terrain on the Terrain layer with a Terrain Collider, the game's TerrainBallistic and `SDK_Terrain.mat`, Unity's **Nature/Terrain/Diffuse** shader, which the game always loads. Retail maps use their own MicroSplat terrain shaders, which only load with their own scenes. Build Map does the first three for every terrain, writes the surfaces, and warns about other terrain shaders and guessed surfaces.
- Painted detail grass works: the game has Unity's grass and billboard shaders. For trees, see [Trees](#trees).

### Trees

Retail trees (Woods' pines) are SpeedTree prefabs on the game's `Nature/SpeedTreeEFT` shader, which the game always loads: a LOD group whose levels each carry the game's `TreeWind`, and an `AICollider` child on the Foliage layer with the game's `TreeInteractive` and trigger spheres down the canopy (the rustle when a player walks through, and what tells bots a player is in the tree). It holds the trunk's capsule (LowPolyCollider: blocks movement) and a mesh collider of the bark with a wood BallisticCollider (HighPolyCollider: bullets).

- **GameObject > Tarkov SDK > Environment > Tree (Pine)** and **Tree (Broadleaf)** place the SDK's own trees, built that way. Their meshes, textures, materials and prefabs are generated the first time into `Assets/TarkovSDK/Trees`.
- **Tarkov SDK > Decompilation > Tree Builder** makes the same out of any tree: a tree from another map (imported with **Import AssetRipper Export**: its SpeedTree meshes, materials, keywords and wind values are kept) or your own model (each material goes on the tree shader as **bark** or **leaves**, from its texture's transparency, render queue and name; leaves are alpha-cut and two-sided). It adds the LODs (from the source's LOD group, or from children named `..LOD0`, `..LOD1`...), the trunk, the bark hitbox and the canopy triggers, sized from the model, and saves a prefab. The source isn't changed. **Add To Terrains** puts the tree on the scene's terrains to paint with Unity's **Paint Trees** tool: painted trees render and collide, but the canopy triggers only work on placed ones.
- The editor's tree shader (`Tarkov/Nature/SpeedTreeEFT`) is Unity's own SpeedTree shader under the game's name, lit by Unity's lighting rather than the raid's; the raid uses the game's. SpeedTree's shader moves vertices towards their second UV set as an LOD fades out, so the Tree Builder copies non-SpeedTree meshes with that set holding their own positions, and their LODs don't fade (they'd fold up otherwise). Generic models (the SDK's trees, models through the Tree Builder) have none of SpeedTree's branch and leaf wind data, so they sway as a whole: the tree bends with the wind, more towards the top, with the game's own global wind and retail Woods' values (a pine's or a broadleaf's, from the tree's name), through a `TreeWind` on each LOD. Build Map adds that to trees made before, and the SDK's own tree prefabs are updated the next time you place one or build.
- Build Map fills the scene's `LocationScene` tree wind presets from every `TreeWind`, as the game's own preprocess does, so LevelSettings gives each tree its wind.

### ProBuilder, Polybrush and vertex painting

**Tarkov SDK > Install Packages > ProBuilder (5.2.4)** installs Unity's ProBuilder for building geometry in the Scene view, and sets it up for Tarkov once it's loaded. **Quick Fixes > Set Up ProBuilder For Tarkov** does that again: new ProBuilder shapes get `SDK_Default.mat` (the p0 SMap stand-in) instead of ProBuilder's own material, the open scenes' objects still on ProBuilder's shaders are moved to it, and ProBuilder's **Vertex Colors** window gets the vertex-paint palette. Build Map needs nothing special: Unity's build saves the ProBuilder meshes into the map and strips ProBuilder's scripts, which the game doesn't have. For bullets and walking, give ProBuilder objects LowPolyCollider + HighPolyCollider colliders (on the object or on children of it), or a Mesh Collider on the Terrain layer (**Make Selected Static Geometry**); Build Map only warns about ProBuilder objects with no solid collider at all, and objects on ProBuilder's shaders.

**Vertex painting** uses the game's vertex-painted ground shader, **Custom/Vert Paint Shader Solid**, which blends three texture layers by vertex colour: red is layer 0, green layer 1, blue layer 2, and mixed colours blend them (each layer's **Heights** map shapes the blend; **Blend Strength** sharpens it). **Tarkov SDK > Tools > Geometry > New Vertex Paint Material (On Selected)** makes one next to the scene with three tinted layers (concrete, dirt, grass) and puts it on the selected objects; set each layer's textures. Then paint with ProBuilder's **Vertex Colors** window (Tools > ProBuilder > Vertex Colors), whose palette now holds the pure layer colours and their mixes. Collision uses one surface per object, as for any mesh, so pick its BallisticCollider's material for the main layer.

**Polybrush** (**Tarkov SDK > Install Packages > Polybrush (1.2.1)**) sculpts, smooths, paints and scatters prefabs on any mesh, ProBuilder's included. Its **Texture Blend** brush knows the vertex-paint shader's layers: on an object with a vertex paint material it shows the material's three textures, and painting one paints its layer. The **Vertex Color** brush works too (red, green, blue as above). Build Map keeps the painted and sculpted meshes and leaves Polybrush's own component out of the map, since the game doesn't have it.

### Using assets from the game

Meshes, textures, materials and prefabs from Tarkov's own maps can be used through an [AssetRipper](https://github.com/AssetRipper/AssetRipper) export. You don't need to export the whole game: [this list of the retail map files](https://gist.github.com/DrakiaXYZ/0c392b4ad6781f287e3f281bdc79e70e) says which `levelN` files in `EscapeFromTarkov_Data` make up each map (Customs is `bigmap`), so open only those (AssetRipper loads what they depend on by itself) and **Export > Export All Files** as a Unity project. Don't copy the export into `Assets`: its decompiled game scripts clash with the SDK's stubs and the project stops compiling, and with it every SDK menu. **Tarkov SDK > Decompilation** brings it in instead:

- **Import AssetRipper Export**: **Read Export** on the folder AssetRipper wrote to (it finds `ExportedProject/Assets`), search and pick prefabs, models, materials, textures or scenes, and **Import**. **Bring what they use** adds every mesh, material, texture, sound, animation and prefab they reference. On the way in:
  - components move onto the project's scripts of the same class, whether AssetRipper wrote its scripts as decompiled `.cs` files or kept them in their DLLs (found by Unity's FileID of the class): the SDK's stubs for the game's components (same fields, so their values stay) and the packages' for theirs. Components the SDK has no stub for are listed in the Console and, with **Remove scripts the SDK doesn't have** on, removed from the prefabs.
  - materials on a game shader the SDK has a stand-in for move onto it (`Tarkov/...`). Game shaders without one come in renamed `Tarkov/<name>`, so the raid still swaps the game's real shader in; with **Keep game shaders the SDK has no stand-in for** off, those materials go on Unity's Standard instead.
  - assets keep their GUIDs, so everything that pointed at them still does; what the project already has isn't copied again.
- **Clean Up Ripped Scene...** (with the ripped scene open): **Scan** lists what needs fixing and **Clean** fixes the ticked ones, with Undo: components whose script didn't come across, materials on game shaders the SDK has a stand-in for, tree impostors (retail's far billboards; their shader and script are retail-only), renderers and mesh colliders without a mesh, LOD groups pointing at removed renderers, cameras and audio listeners left in the scene, and terrains on retail terrain shaders (they get `SDK_Terrain`). It lists what it can't fix: statically batched meshes (export again with AssetRipper's **Static Mesh Separation** on), scene singletons the map has more than one of (LocationScene, LevelSettings, the weather, EnvironmentManager), and a retail spatial audio system.
- **Prefab Library...**: **Extract From Open Scenes** turns the important objects of a ripped (or any) scene into prefabs, one per distinct object (500 identical crates make one prefab): doors with their frames, containers, switches, lamps, windows, mounted guns, explosives, fires, trees and props (and, ticked, buildings over 12 m). They go in the map's folder (its Map Definition's, else the scene's) under `Prefabs/<Category>/`, with their ids cleared so Build Map gives every copy its own; meshes that only lived in the scene are saved under `Prefabs/_Meshes`. **Make Prefabs From Selected** does it for the selected objects, into the category picked beside it (**Selected become instances** links them to the new prefabs). The window shows them by category with thumbnails and a search: drag one into the Scene view, or double-click to place it where the view looks.
- **Fix Ripped Materials In Selected Folder** and **Remove Missing Scripts In Selected Folder** do the same to a folder already in the project (select it in the Project window).
- **Tree Builder**: see [Trees](#trees).

What you take from the game goes into your map's bundle; keep that in mind before sharing a map built with it.

### Help

**Tarkov SDK > Help** has a tab per topic (starting out, building, spawns and AI, extracts, triggers, events, map features, lighting, audio, decompilation), a troubleshooting list, and **Errors**: every message Build Map logs, with what it means and how to fix it. After a build it explains that build's own messages; paste any Console message into it to find its entry. Build Map's error and warning dialogs open it with **Explain Them**.

### Building

The **Build Map** button:

1. opens the map's scenes, fills `LocationScene` arrays, syncs spawn points into their bot zones, links patrol points, lowercases exfil entry points, and stamps `LocationExportInfo`;
2. writes `Assets/TarkovSDK/Generated/<id>/<id>.ScenesPreset.asset`;
3. builds `maps/<id>.bundle` and `maps/<id>_scenes.bundle` into `Build/`;
4. writes `Build/maps/<id>/sdk_export.json`;
5. if **Deploy To Game** is ticked, copies the bundles to `BepInEx/plugins/CustomMapLoader/bundles/maps/` and the export to `SPT_Runtime/user/mods/CustomMapLoader/maps/<id>/`, and deploys the map's pack if it is in the project. These folders are fixed; the build warns if CustomMapLoader itself isn't installed there.

Restart the SPT server after a build, because it reads `sdk_export.json` at startup. Warnings and errors from the build appear in the Unity console.

### Sharing a map

**Create Package** (on by default) writes `<Output Folder>/<MapId>.zip`: the map bundles, `base.json`, `locale.json`, `sdk_export.json`, the map select settings and pictures (`menu.json`, `images/`), the map's pack if it has one, and a short readme, all laid out like an SPT install. It contains no DLLs. Players install CustomMapLoader once, then extract map packages into their SPT folder.

The server files are taken from the SPT install the definition deploys to, so set them up with **Edit Server Data** first.

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

Reading the 378 MB shader bundle takes a few minutes. Shaders that have a decompiled file in `Shaders/Game/` are skipped; those are maintained by hand from the game's compiled code with the tools in `Tools/ShaderStandins/decompile/`:

```sh
python Tools/ShaderStandins/decompile/extract.py C:/spt41/EscapeFromTarkov_Data/StreamingAssets/Windows/shaders cache Tools/ShaderStandins/shaders.txt   # pull the shaders out of the bundle
python Tools/ShaderStandins/decompile/dump.py cache/p0_Reflective_Bumped_Specular_SMap.pkl out/smap                                                # annotated D3D listings per variant
python Tools/ShaderStandins/decompile/hlslcheck.py Unity/TushonkaMappingSDK/Assets/TarkovSDK/Shaders/Game/p0_Bumped_Specular.shader                 # compile-check a stand-in against Unity's includes
```

`hlslcheck.py` compiles every CGPROGRAM of a `.shader` with Windows' own `d3dcompiler_47.dll` and Unity's `CGIncludes`, for the keyword sets its pragmas and pass type imply, and reports errors with the file and line, so stand-ins can be checked without opening Unity. `DECOMPILE.md` explains what each decompiled shader computes.
