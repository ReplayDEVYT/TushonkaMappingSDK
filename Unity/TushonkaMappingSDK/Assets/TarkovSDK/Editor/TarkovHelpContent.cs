using System.Collections.Generic;

namespace TarkovSDK.Editor
{
    // The Help window's text (Tarkov SDK > Help): how-tos per tab, and every message Build Map logs with what it means
    // and how to fix it. A message is matched to its entry by the entry's Key, a phrase from the message's fixed text.
    // When Build Map gets a new message, add it here.
    public static class TarkovHelpContent
    {
        public sealed class Section
        {
            public string Title;
            public string Text;
            // a web page the section points to, shown as a link under it
            public string Link;
        }

        public sealed class Tab
        {
            public string Name;
            public Section[] Sections;
        }

        public enum Severity { Error, Warning, Dialog }

        public sealed class Message
        {
            public string Area;
            public string Key;
            public Severity Severity;
            public string Meaning;
            public string Fix;
        }

        private static Section S(string title, string text) => new Section { Title = title, Text = text };

        public static readonly Tab[] Tabs =
        {
            new Tab
            {
                Name = "Start",
                Sections = new[]
                {
                    S("Your first map",
                      "1. Tarkov SDK > Map Settings > New Map..., type a map id (lowercase letters, digits, underscores). It makes Assets/Maps/<id>/ with a scene, an example layout showing every feature, and a Map Definition.\n" +
                      "2. Select the Map Definition, press Edit Server Data, then Create From sdk_example Template (a base.json with your id and a new _Id).\n" +
                      "3. On the Map Definition tick Deploy To Game, check Spt Root (your SPT folder), and press Build Map.\n" +
                      "4. Restart the SPT server (it reads the map at startup) and start the game. The map is on the map select screen."),
                    S("What goes where",
                      "Map bundles: BepInEx/plugins/CustomMapLoader/bundles/maps/<id>.bundle and <id>_scenes.bundle (+ <id>_audio/).\n" +
                      "Server data: SPT_Runtime/user/mods/CustomMapLoader/maps/<id>/ (base.json, locale.json, sdk_export.json, menu.json, images/).\n" +
                      "The CustomMapLoader client plugin and server mod must be installed, the same version as the SDK."),
                    S("The menus",
                      "Tarkov SDK in the menu bar: Build Selected Map; Generation (things made for the whole map: navmesh, indoor triggers, patrols, audio, weather, rain cover, airdrops, BTR roads); " +
                      "Tools (windows: extracts, loot containers, triggers, world events (BTR Path Editor, mortar shelling, scripted events), geometry (wire tool, player scale, culling helper, city builder), lighting, and the Asset Importer); Map Settings (new map, map pack, Server Data Editor); " +
                      "Decompilation (AssetRipper import, Clean Up Ripped Scene, ripped material fixes, Prefab Library, Tree Builder); Quick Fixes; Install Packages; Help.\n" +
                      "GameObject > Tarkov SDK (also the Hierarchy's right-click menu) places objects: spawns, exfils, AI, interactables, hazards, lights, sound, environment (terrain, trees, fog, wires), quests, " +
                      "world events (airdrops, BTR, mortar, scripted events) and triggers (shootable triggers and what a trigger id does)."),
                    S("Layers in one line",
                      "Terrain = everything solid; LowPolyCollider = blocks movement only; HighPolyCollider = bullets and AI sight only (with a BallisticCollider); Triggers = spawns, exfils, zones. Colliders on Default are ignored by bullets and bots."),
                },
            },
            new Tab
            {
                Name = "Building",
                Sections = new[]
                {
                    S("Build Map",
                      "Select the Map Definition and press Build Map (or Tarkov SDK > Build Selected Map). It prepares every scene (ids, arrays the game reads, spawn points, exfils, checks), " +
                      "writes the bundles and sdk_export.json, deploys them if Deploy To Game is on, and zips a package if Create Package is on. Errors stop the build; warnings don't. " +
                      "Every message is explained in this window's Errors tab."),
                    S("Several scenes",
                      "A map can be split into scenes (an area each, a building, the sound), all loaded together in the raid: list them in the Map Definition's Scenes, the first one first. " +
                      "The Map Definition's Scenes buttons: Open All Scenes, Add (a new scene with its LocationScene, added to the map), Fix Scene Setup. " +
                      "The first scene holds what a map has once (LevelSettings, the time of day, the EnvironmentManager, the ambient audio system) and every indoor trigger, since the game only checks those there; " +
                      "Build Map moves them from the other scenes and keeps ids (doors, lamps, windows, spawn points, exfil names, quest zones) unique across all of them. " +
                      "Unity doesn't save references between scenes, so a switch and the door it opens belong in the same scene; trigger ids work across scenes. " +
                      "Bake the navmesh, rain cover, indoor triggers and spatial audio with all the scenes open."),
                    S("The player's size",
                      "Tarkov SDK > Tools > Geometry > Player Scale... has the game's sizes: 1.8 m tall, a 1.6 m collision capsule standing (1.2 m crouched, 0.3 m prone, 0.73 m wide), " +
                      "steps up to 0.2 m, slopes up to 50 degrees, vaults over 0.25 to 1.1 m (up to 0.41 m deep), climbs onto 1.31 m, falls 3 m without damage. Retail doors are 2.1 to 2.2 m tall. " +
                      "A figure follows the mouse in the Scene view or is placed (EditorOnly); Measure takes two clicks and says whether the player can step, vault, climb or drop between them; " +
                      "Measure The Open Scenes puts the map's size next to the retail maps'."),
                    S("Culling (drawing less)",
                      "Tarkov SDK > Tools > Geometry > Culling Helper... does what retail maps do so the raid doesn't draw what can't be seen. Occlusion culling (Factory, Labs, Shoreline, Interchange): " +
                      "Mark Static Objects, Bake with all map scenes open, then Turn On In base.json (OcculsionCullingEnabled) and restart the server; bake last, since what moves after the bake can vanish. " +
                      "Lights: a CullingLightObject on every light, off beyond a distance. Small objects: an LOD group that culls below 2% of the screen, as on Customs."),
                    S("A city (City Builder)",
                      "Tarkov SDK > Tools > Geometry > City Builder...: New City, New Kit, then fill the kit with materials (roads, sidewalks, kerbs, block and park ground, markings) and prefabs " +
                      "(buildings, lamps, street props, cars, trees, park props), yours or Tarkov's: in the Asset Importer pick objects or materials and press Add To City Kit. " +
                      "Set each prefab's Front (which way its door side or bonnet points). Generate lays out blocks and streets from the seed and builds the ground (colliders, asphalt and concrete surfaces), " +
                      "buildings along the streets, lamps, props, parked cars and trees. Generate replaces what it built before (not undoable); bake the navmesh after. " +
                      "By hand: the Roads tab draws roads by clicking points (a smooth curve with sidewalks, kerbs, lane lines, lying on the ground) and places junctions (R turns them; on slopes Fit To Ground tilts one to the ground); " +
                      "a road that ends on a junction's arm joins it. Drag a selected road's points, Ctrl-click it to add one; the curve editor (Roads tab) gives each point a mode (Smooth, Aligned, Free, Corner; right-click a point) with blue handles to drag, and a height profile to raise or lower the road at each point. " +
                      "Intersections: Place Junctions on a road's middle cuts it into a crossroads, drawing a road into another's middle makes a T, Intersect Crossing Roads does every crossing. Line This Road fills its sides with the kit's buildings, lamps, trees, props and cars. " +
                      "The Place tab is a brush of the kit's prefabs: with Snap To Roads buildings stand behind the nearest road's sidewalk facing it, cars park at the kerb."),
                    S("After a build",
                      "Restart the SPT server: it reads base.json, locale.json and sdk_export.json only at startup. The game itself can stay closed or be restarted; the bundles are read when a raid loads."),
                    S("Where the logs are",
                      "Unity: the Console (Build Map's own messages start with [Tarkov SDK]).\n" +
                      "Game: BepInEx/LogOutput.log (CustomMapLoader's lines start with [CustomMapLoader]; turn on verbose logging in its config for more).\n" +
                      "Server: the SPT server window or its log file ([MappingSDK] lines)."),
                    S("Sharing",
                      "Create Package writes <Output Folder>/<MapId>.zip laid out like an SPT install. Players install CustomMapLoader once, then extract map packages into their SPT folder."),
                },
            },
            new Tab
            {
                Name = "Spawns & AI",
                Sections = new[]
                {
                    S("Spawn points",
                      "GameObject > Tarkov SDK > Spawns. Put spawn points under a Bot Zone to give them to it. PMC player spawns need an Infiltration (the entry point name), which extracts list in Entry Points."),
                    S("Bots",
                      "Bake the navmesh (Generation > Essential > Bake NavMesh): bots only walk on it, and the client builds their cover, core points and patrols from it at raid start. " +
                      "Waves and bosses are in the Server Data Editor's Bots tab (base.json). Keep areas bot-free with AI > Nav Blocker and rebake."),
                    S("Bots that spawn on a trigger",
                      "GameObject > Tarkov SDK > Triggers > Spawn Bots, or Triggers & Sounds' Spawn bots: a role (scav, PMC, raider, rogue, cultist, a boss), difficulty and count, at the object's child points, when a zone or switch fires. " +
                      "They spawn like any bot (profiles from the server) into the nearest bot zone. Points must be on the navmesh. In Fika co-op they're spawned by the host."),
                    S("Loot",
                      "Spawns > Loot Spawn is loose loot rolled at raid start. Containers get the loot retail maps put in their type, or your own (Tools > Interactables > Loot Containers). " +
                      "Triggers > Spawn Loot drops items when a trigger fires, at the spawner itself (At This Object) or at its child points (weapons come built from their default preset); in co-op only the host sees them."),
                },
            },
            new Tab
            {
                Name = "Extracts",
                Sections = new[]
                {
                    S("Extracts and transits",
                      "Tools > Extracts > Extracts... lists them, makes every kind (regular, scav, co-op, car, flare, elevator) and edits the selected one's rules (time, chance, requirement, entry points). " +
                      "Every extract needs a trigger collider and a unique name; the game reads its rules from the server by that name."),
                    S("Transits",
                      "Exfils > Transit Point: set parameters.location to the destination map id. Forced Transit sends the player at once, without the menu."),
                },
            },
            new Tab
            {
                Name = "Triggers",
                Sections = new[]
                {
                    S("Trigger ids",
                      "Zones send a trigger id when a player enters or leaves; switches and doors send the ids in their Triggers Map when they open or close; shootable triggers send one when shot or caught in a blast; " +
                      "world events send them from their steps. Listeners (sounds, lamps, doors, animations, explosions, the SDK's events) react to ids. An object's Id is not a trigger id."),
                    S("Shootable triggers and explosions",
                      "Triggers > Shootable Trigger on a selected object (or Triggers & Sounds' When: An object is shot): the game's TriggerBallistic fires its id when the object's BallisticCollider is hit, or when an explosion's blast reaches it, so blasts chain. " +
                      "Triggers > Explosion (or Do: Set off an explosion): the game's HandlerExplosion goes off once on its id, with retail's gas cylinder blast by default (radius, fragments, effect in its inspector). " +
                      "Hazards > Explosive Barrel is both on one object, as Interchange's red gas cylinders, and hides the cylinder when it blows. A shot only fires on the shooter's machine, as in retail."),
                    S("Connecting things",
                      "Tools > Triggers > Triggers & Sounds: pick When (a zone, a switch, an object that's shot) and Do (play a sound, lamps, doors, animations, force a transit, strip gear, a notification, captions, health, effects, " +
                      "spawn bots, spawn loot, spawn / show / hide a prefab, shell a mortar zone, set off an explosion, start a world event), press Connect. Below it lists every id; red ones never fire."),
                    S("Spawn, show or hide a prefab",
                      "Triggers > Spawn Prefab: Spawn puts a copy of a prefab (or scene object) at each child point; Show switches a scene object on, Hide switches it off. " +
                      "A prefab asset is copied into the scene, switched off, under SDK_SpawnTemplates on every build, so it ships. Use it for props, lights, fire, smoke, sounds, physics props; " +
                      "doors, containers, switches, extracts and loot need the raid's registration at start and don't work spawned later."),
                },
            },
            new Tab
            {
                Name = "Events",
                Sections = new[]
                {
                    S("BTR",
                      "World Events > BTR Route, then Tools > World Events > BTR Path Editor. Stops are where it pauses (and the taxi goes); a route is its stops in order, where it comes from and where it leaves to. " +
                      "Give a looping route at least three stops: with two it drives the same road back and forth and turns round at every stop.\n" +
                      "Roads: Regenerate All (drive-through) turns every stop to face the way the BTR drives and makes each road leave and enter its stops along those arrows, so it drives straight on. " +
                      "The game only makes the three-point turn when the next road starts behind the BTR, and slides sideways when a road turns 90 degrees or more under it.\n" +
                      "Edit a road point by point in the Roads tab (move points and their yellow handles, insert, delete, smooth, snap to ground, keep off walls); edited roads are kept when the others are regenerated.\n" +
                      "Preview plays a route as the game drives it, marking turns (orange), slides (red) and dead ends; Check Routes does every route at once, as Build Map does."),
                    S("Mortar shelling",
                      "World Events > Mortar Zone (and Mortar Battery), Tools > World Events > Mortar Shelling. A zone is a grid of target points (red); a battery is the guns the shells fly from (orange, a few hundred metres off the map). " +
                      "Planned shelling strikes random planned zones by itself; the blue signal flare (the game's mortar call) strikes the player who fires it, sized like the Called Strike Template zone; a zone's Trigger Id strikes it from a zone or switch. " +
                      "The siren, the radio detector's alarm, the shell, its sound and explosion are the game's own. Preview drops a round in the Scene view: crosses with no ground under them mean shells that would land at the world origin."),
                    S("Airdrops",
                      "World Events > Airdrop Point, or Generation > Misc > Airdrop Points. Plane drops are in the Server Data Editor's Airdrops tab; keep Min Players at 1 for solo raids."),
                    S("Raid events",
                      "Triggers > Gear Strip, Notification, Caption, Health Change, Effect, Spawn Bots, Spawn Loot, Spawn Prefab: CustomMapLoader's own, run by its client from a trigger id. They need the same version of the client and server mods as the SDK."),
                    S("Scripted world events",
                      "World Events > Scripted Event, edited in its inspector or Tools > World Events > Scripted Events...: it starts at a random raid time between Earliest and Latest Minute, on a trigger id, or whichever comes first, " +
                      "with a chance per raid, and can repeat. Its steps run in order, each after its wait: fire a trigger id (anything connected to it reacts: explosions, mortar zones, spawns, lamps, doors, sounds), " +
                      "show or hide a scene object, play or stop a scene AudioSource (Play On Awake off), notify every player. The window's buttons add a step together with what it fires (an explosion, a mortar strike, bots, loot, a prefab). " +
                      "In co-op every player's game runs it with the same rolls; bots, loot and shelling still happen on the host only."),
                },
            },
            new Tab
            {
                Name = "Map Features",
                Sections = new[]
                {
                    S("Fire",
                      "Hazards > Fire (flames for your own brazier or wreck) and Fire Barrel (in a generated steel drum): particle flames, smoke and sparks, a flickering light (the game's FlickeringLights) and a burn box with the game's FlameDamageTrigger, " +
                      "as retail's bonfires. The textures are generated and the materials use Unity's legacy particle shaders, which the game always has. Add a fire loop with a Sound Zone if you have one."),
                    S("Container groups",
                      "Interactables > Container Group (with containers selected, they go into it): each raid spawns between its Min and Max of the containers under it, picked by their Spawn Chance, as retail's container groups. " +
                      "The rest aren't in that raid. A gear strip's stash always spawns."),
                    S("Mounted guns",
                      "Interactables > Mounted Gun (Utes): the game's own NSV Utes is placed in the raid where the marker stands (the gunner's spot, facing where it fires; it turns 20 degrees either way), with a crate platform under its tripod, " +
                      "as retail Customs' sandbagged guns. The Scene view draws the game's own gun there, read from your game install (Show The Gun in the marker's inspector). Mounted guns from a ripped retail scene (Utes or AGS) work too. Bots don't man them.\n" +
                      "Interactables > Mounted Gun (AGS-30): the game's AGS-30 on its tripod, feet on the ground, firing along the object's forward. The first one is read from your game install's " +
                      "retail maps into Assets/TarkovGameAssets/Prefabs/Mounted Guns (it exists only there); it's the game's own gun, so it works in the raid as a ripped one does."),
                    S("Breachable doors",
                      "Interactables > Breachable Door, or Tools > Interactables > Make Selected Doors Breachable: kicking the door in (Breach) splinters it as retail's wooden doors do: chips and dust burst from the lock, " +
                      "splinters drop, and broken wood shows round the lock. Any shut or locked door with Can Be Breached can be kicked open; this adds the splintering (the game's BrokenDoor)."),
                    S("Volumetric light shafts",
                      "Lights > Volumetric Light Shaft (a soft-shadowed spot through a window), or Tools > Lighting > Add Volumetric Light To Selected Lights: the game's VolumetricLight with retail's values. " +
                      "In the raid it shows with Volumetric Lighting on in the game's graphics settings. The Scene view previews the haze, but without the light's shadows, so shafts aren't cut by walls there."),
                    S("Birds",
                      "GameObject > Tarkov SDK > Environment > Birds (Flight Paths): crows circling two loops over the spot; Bird Flock: a flock circling a point 35 m up. Both use the game's own bird code with the SDK's crow or gull. " +
                      "Tarkov SDK > Tools > Geometry > Bird Path Tool... draws loops by clicking (each point Height above the ground), adds circles, moves points (sphere across, arrow up and down) and marks in red where a path goes through something. " +
                      "Paths are always closed loops, since a bird goes from a path's end back to its start. Preview Flight / Preview Flock on the birds flies them in the Scene view. Keep flocks at 5 birds or more: a flock bird with too few others near it stops in the air."),
                    S("Wires",
                      "Environment > Wire, or Tools > Geometry > Wire Tool... to click points in the Scene view: sagging wires between the points (several side by side per span), with wooden poles and crossarms under them if you like. " +
                      "Move a point and the wires follow. They're a plain mesh, as retail's, on the game's swaying wire shader: they swing in the wind, most at mid-span. " +
                      "Preview Wind (Time of Day / Weather window, Lighting Editor) shows it in the Scene view."),
                    S("Previews",
                      "Fires, shootable triggers, explosions, breachable doors, container groups and scripted events have a Preview panel in their inspector, like doors and lamps: Light a fire, Shoot It, set off an explosion " +
                      "(the blast sets off the shootable triggers it reaches, as in the raid), kick a door in, Roll A Raid for a container group, Play an event from the start (sped up if you like). Stop & Restore puts the scene back."),
                },
            },
            new Tab
            {
                Name = "Lighting",
                Sections = new[]
                {
                    S("Time of day",
                      "Generation > Time > Time of Day / Weather...: the simple rig (Factory's) or the full weather system (Customs'). Without one the map renders black in the raid; Build Map adds the simple one."),
                    S("Too bright at night",
                      "Quick Fixes > Fix Night Lighting (Unity's ambient light glows on every surface day and night) and Fix Shiny Materials (p0 materials without a reflection cubemap shine grey)."),
                    S("Shaders",
                      "Use the shaders under Tarkov/: stand-ins for the game's own, swapped for the real ones in the raid. Unity's Standard also works. Other shaders ship as they are and may not render in the game."),
                },
            },
            new Tab
            {
                Name = "Audio",
                Sections = new[]
                {
                    S("Ambience",
                      "Generation > Ambience > Ambient Sounds: outdoor day/night, wind, rain and indoor beds, or the game's own Ambient Audio System."),
                    S("Spatial audio",
                      "Generation > Audio > Spatial Audio...: rooms and portals (doors, windows, openings), room tones, bunkers, and Meta XR room acoustics (needs the Meta XR Audio SDK 71.0.0). " +
                      "Fix Audio Sources sets every AudioSource up for the game's spatializer."),
                },
            },
            new Tab
            {
                Name = "Decompilation",
                Sections = new[]
                {
                    S("Asset Importer (objects from the retail maps)",
                      "For single objects from Tarkov's maps (a door, a crate, a lamp, a tree), use Tarkov SDK > Tools > Asset Importer... instead of a rip: it reads them straight from the game install " +
                      "(the Map Definition's Spt Root, or Change...). Pick a map and press Read (once; it's remembered), pick a kind (doors, containers, switches, lamps, windows, mounted guns, explosives, fires, particles, trees, " +
                      "props, buildings, materials, sounds, data; the Effects tab has the game's explosions and impacts, each effect whole), and Import the objects you pick, or drag one into the Scene view. Each becomes a prefab in the map's Prefabs/<Category>/ (the Prefab Library shows it) with its meshes, " +
                      "materials, textures, colliders, lights, LOD groups, particle systems and game components with their values; the assets go in Assets/TarkovGameAssets/. Components the SDK has no class for are left out " +
                      "(the Console lists them). Materials come in as .mat files (drop one on an object to put it on); sounds play with a double-click and come in as .wav files (drop one in the Scene view for a sound source). " +
                      "Data: a map's Lighting is applied to the open scenes' LevelSettings, time of day and ambient light (values only, Undo reverts it); its PRISM preset is made from the map camera's post-processing and can go on LevelSettings' Prism Preset Prefab. " +
                      "The rest of this tab is for ripping entire maps."),
                    new Section
                    {
                        Title = "Using assets from the game",
                        Text = "To rip entire maps and their assets, rip them with AssetRipper, then use Tarkov SDK > Decompilation > Import AssetRipper Export. You don't need the whole game: the list below says which levelN files " +
                               "in EscapeFromTarkov_Data make up each retail map (Customs is bigmap); open only those in AssetRipper (File > Open File; it loads what they depend on by itself) " +
                               "and Export > Export All Files as a Unity project. Don't copy the export into Assets yourself: its decompiled scripts clash with the SDK's game stubs and the project stops compiling (and then no SDK menu works).",
                        Link = "https://gist.github.com/DrakiaXYZ/0c392b4ad6781f287e3f281bdc79e70e",
                    },
                    S("Import AssetRipper Export",
                      "Read Export indexes the rip. Search and pick prefabs, models, materials, textures or scenes (500 a page; Pick All Matching takes every page); Bring what they use adds every mesh, material, texture, sound, animation and prefab they reference. " +
                      "On import, components are moved onto the SDK's stubs (same fields, so their values stay), materials onto the SDK's shader stand-ins, and game shaders without a stand-in come in renamed Tarkov/<name> " +
                      "so the raid swaps the real one in. Scripts the SDK has no stub for are listed in the Console and removed from the prefabs."),
                    S("Clean Up Ripped Scene",
                      "Decompilation > Clean Up Ripped Scene... with the ripped scene open: Scan lists what needs fixing, Clean fixes the ticked ones (missing scripts, materials onto stand-ins, tree impostors, renderers and colliders without meshes, " +
                      "LOD groups pointing at nothing, cameras and listeners, terrains on retail shaders). It also lists what it can't fix: statically batched meshes (export again with AssetRipper's Static Mesh Separation), " +
                      "scene singletons the map has twice, a retail spatial audio system. Save the scene afterwards."),
                    S("Prefab Library",
                      "Decompilation > Prefab Library...: Extract From Open Scenes turns every distinct door, container, switch, lamp, window, mounted gun, explosive, fire, tree and prop of a ripped scene into a prefab " +
                      "(one per distinct object), in the map's folder under Prefabs/<Category>/. Make Prefabs From Selected does it for the selected objects. Browse them by category, search, a page at a time (only what's on screen loads), and drag one into the Scene view " +
                      "(double-click places it at the view's centre). Ids are cleared, so Build Map gives each placed copy its own."),
                    S("Fix folders already in the project",
                      "Select a folder in the Project window: Decompilation > Fix Ripped Materials In Selected Folder (stand-ins and Tarkov/ renames), Remove Missing Scripts In Selected Folder (it also deletes the empty TextMeshPro fonts older imports brought in, which make TMP throw NullReferenceException in ReadFontAssetDefinition). Components left off at import (no class in the SDK) come back with their game values from the import window's Restore Components, once the project has the class (stub-roots.txt + regenerate, or your own script of the same name); it needs the export still on disk. Decompilation > Adjust Prefabs... repairs a folder of ripped prefabs (layers, ballistics, interactable settings, lamps, windows, ids, collision) and rebuilds doors, containers and lamps that lost their game components; Preview first."),
                    S("Trees",
                      "GameObject > Tarkov SDK > Environment > Tree (Pine / Broadleaf) places the SDK's own trees. Decompilation > Tree Builder turns any tree into a game-ready one: a tree imported from another map keeps its SpeedTree meshes, " +
                      "materials and wind; any other model gets its bark and leaves on the game's tree shader (Nature/SpeedTreeEFT), and every tree gets LODs, a trunk that blocks players and bullets (wood), and the canopy triggers that rustle " +
                      "and tell bots a player is inside, laid out as on Woods. Add To Terrains puts it on the scene's terrains to paint with Paint Trees (painted trees render and collide, the canopy triggers need placed trees)."),
                    S("Shipping game assets",
                      "Assets taken from the game go into your map's bundle. Keep that in mind before sharing a map built with them."),
                },
            },
            new Tab
            {
                Name = "Troubleshooting",
                Sections = new[]
                {
                    S("The map isn't on the map select screen",
                      "The SPT server wasn't restarted, base.json's Id doesn't match the map id (the server skips it), its _Id clashes with another map's (the server log says so), or the server mod isn't installed. Check the SPT server's [MappingSDK] lines."),
                    S("The raid never loads / black screen at load",
                      "Look at BepInEx/LogOutput.log. Common causes: the bundles weren't deployed (Deploy To Game off, wrong Spt Root), an audio portal joining one room, AirdropParameters empty, a missing LocationScene, or the client and server mods being different versions."),
                    S("Everything is black in the raid",
                      "No time of day system (Build Map adds one), or LevelSettings with no fog mode. Build Map fixes both and says so; build again."),
                    S("Bots stand still",
                      "No navmesh, spawn points off the navmesh, or bot zones with no reachable points. Bake the navmesh after moving geometry."),
                    S("The BTR slides sideways or turns round at every stop",
                      "Its roads leave stops behind where it faces. Open the BTR Path Editor, press Regenerate All with Drive-through on, and Check Routes. A two-stop loop always turns round."),
                    S("Pink materials",
                      "The material's shader is missing: in a rip, the shader didn't come with it. Run Decompilation > Fix Ripped Materials, or pick a Tarkov/ shader by hand."),
                    S("Scripts won't compile after importing a rip",
                      "Decompiled game scripts got into Assets. Delete the ripped Scripts (and Plugins) folders, then use Import AssetRipper Export instead."),
                    S("Mortar shells land in the middle of the map",
                      "Their target points had no ground below them (from 300 m up down to -100 m), so the game used the world origin. Move or shrink the zone; the zone's gizmo and Preview show the points."),
                    S("A mounted gun isn't in the raid",
                      "The server mod and client plugin must be this SDK's version (the server adds its weapon to the loot, the client places it). BepInEx/LogOutput.log says why under 'Mounted guns'."),
                    S("Volumetric light doesn't show",
                      "In the raid it draws only with Volumetric Lighting on in the game's graphics settings, and not on lights at intensity 0. Spot lights need shadows on to make shafts. In the Scene view, check Preview In Scene View on the component."),
                    S("A trigger does nothing",
                      "Open Triggers & Sounds: its list shows every id, what sends and what hears it. A zone needs a trigger collider on the Triggers layer, and the player must be the one to enter it."),
                },
            },
        };

        private static Message M(string area, string key, Severity severity, string meaning, string fix) =>
            new Message { Area = area, Key = key, Severity = severity, Meaning = meaning, Fix = fix };

        private const Severity E = Severity.Error, W = Severity.Warning, D = Severity.Dialog;

        // every message Build Map can log; Key is a phrase from the message's fixed text
        public static readonly Message[] Messages =
        {
            // Build Map itself
            M("Build", "Select a Map Definition asset first", D, "Build Selected Map needs the Map Definition selected in the Project window.", "Select Assets/Maps/<id>/<id>.asset (or make one: Create > Tarkov SDK > Map Definition) and build again."),
            M("Build", "Map Id must be lowercase", D, "The map id names files, folders and the location; the game and the server want lowercase letters, digits and underscores.", "Rename the Map Id on the Map Definition."),
            M("Build", "Add at least one scene to the Map Definition", D, "The Map Definition has no scenes to build.", "Drag the map's scene(s) into its Scenes list. The first is the main scene."),
            M("Build", "Scene names must be unique", D, "Two of the map's scenes share a name; the game loads scenes by name.", "Rename one of the scenes."),
            M("Build", "Asset bundle build failed", D, "Unity couldn't build the bundles.", "Look at the Console's first red error above it: usually a script error, a missing asset or a full disk."),
            M("Build", "is not an SPT install (no EscapeFromTarkov.exe)", D, "Deploy To Game points at a folder without the game.", "Set Spt Root on the Map Definition to your SPT folder (the one with EscapeFromTarkov.exe)."),
            M("Build", "No navmesh found in any map scene", W, "Bots walk only on the navmesh, and the client builds their cover and patrols from it.", "Tarkov SDK > Generation > Essential > Bake NavMesh."),
            M("Build", "but the map has no baked occlusion data", W, "base.json turns on the raid camera's occlusion culling, but there's nothing baked to cull with.",
                "Bake it (Tarkov SDK > Tools > Geometry > Culling Helper: Mark Static Objects, Bake), or turn it off there."),
            M("Build", "but OcculsionCullingEnabled is off in base.json", W, "The map's scenes have baked occlusion culling, but the raid camera only uses it when base.json says so.",
                "Culling Helper > Turn On In base.json, then restart the SPT server. Or Clear the bake if you don't want it."),
            M("Build", "Static objects changed since the occlusion bake", W, "The bake says what hides what; an object moved, added or removed since then can vanish in the raid where the old bake says it's hidden, or show through a wall that's gone.",
                "Bake again with all map scenes open (Culling Helper > Bake)."),
            M("Build", "The map has no SpawnPointMarker", E, "The player has nowhere to spawn, so the raid can't start.", "Add GameObject > Tarkov SDK > Spawns > Spawn Point (PMC)."),

            // fire, explosives, shootable triggers
            M("Explosives", "has no trigger id, so shooting it does nothing", W, "", "Connect it to something in Triggers & Sounds, or remove the shootable trigger."),
            M("Explosives", "has no Target Collider (a BallisticCollider)", E, "A TriggerBallistic fires when its target BallisticCollider is hit.", "Set its Target Collider to a BallisticCollider (on HighPolyCollider) of the object."),
            M("Explosives", "isn't on HighPolyCollider, so bullets and blasts don't reach it", W, "Bullets and the blast's rays only hit HighPolyCollider (and Terrain).", "Move the target collider's object to the HighPolyCollider layer."),
            M("Explosives", "has no trigger id, so it never goes off", W, "", "Connect it in Triggers & Sounds (Set off an explosion), or give it the id of a shootable trigger or world event step."),
            M("Explosives", "has a Max Explosion Distance of 0", W, "", "Set its Radius (Max Explosion Distance) in its inspector; retail's gas cylinder is 10 m."),
            M("Explosives", "has fragments but no Fragment Type item id", E, "The blast's fragments are made from an item.", "Press Reset To Gas Cylinder in its inspector, or set Fragments to 0."),
            M("Explosives", "which had no GameObjectStateSync (the game needs one)", W, "HandlerGOState switches its target through a synced GameObjectStateSync.", "Nothing; it was added."),
            // breachable doors
            M("Doors", "its BrokenDoor has no VFX", E, "BrokenDoor.Breach plays its VFX unchecked.", "Make the door breachable again (remove its BrokenController, then Tools > Interactables > Make Selected Doors Breachable)."),
            M("Doors", "a splinter has no collider", E, "BrokenDoor.Init ignores collisions with each splinter's collider unchecked when the raid starts.", "Give every splinter a collider, or make the door breachable again."),
            M("Doors", "no Door under the BrokenDoor's parent", W, "A door finds its BrokenDoor among its parent's children.", "Keep the BrokenController next to the door leaf, under the door's root."),
            M("Doors", "its door has Can Be Breached off", W, "", "Tick Can Be Breached on the Door."),
            // volumetric lights
            M("Lights", "has no Light on its object (the game requires one)", E, "", "Put the VolumetricLight on the Light's object."),
            M("Lights", "is on a baked area light", W, "", "Use a spot or point light."),
            M("Lights", "its light's intensity is 0, so it never draws", W, "The game skips volumetric lights under 0.001 intensity.", "Raise the light's intensity (lamps switch theirs, so lamp lights are fine)."),
            // wires
            M("Wires", "have no Wires object above them", W, "The wire settings sit under the object that draws the wires.", "Make wires with GameObject > Tarkov SDK > Environment > Wire or the Wire Tool."),
            M("Wires", "has fewer than two points, so it has no wires", W, "", "Add points (the settings' Add Point, or Continue in the Wire Tool)."),
            // container groups
            M("Loot", "has no containers under it", W, "A container group's containers are the ones under it.", "Put Lootable Containers under it (select them and use its inspector's Move button)."),
            M("Loot", "Max is now", W, "Max can't be below Min.", "Nothing; check the group's numbers."),
            M("Loot", "some raids get all of them", W, "Max is more than the group has.", "Lower Max or add containers."),
            M("Loot", "has Max 0, so its containers never spawn", W, "", "Raise Max."),
            // mounted guns
            M("Mounted guns", "so its gunner floats", W, "The marker is where the gunner sits; nothing is under it.", "Press Drop To Ground on the marker, or put ground under it."),
            M("Mounted guns", "below the ground, where its gunner sits", W, "", "Press Drop To Ground on the marker."),
            M("Mounted guns", "is tilted; the game's prefab stands upright", W, "", "Turn the gun about Y only."),
            M("Mounted guns", "(a ripped gun without its parts", E, "A StationaryWeapon reads these parts unchecked when a player takes it.", "Rip the gun again with its whole hierarchy, or remove it."),
            // world events
            M("World events", "starts on a trigger but has no trigger id, so it never runs", W, "", "Connect it in Triggers & Sounds (Start a scripted world event), or start it at a raid time."),
            M("World events", "can start on a trigger but has no trigger id; only its raid time starts it", W, "", "Connect it, or set Start to Raid Time."),
            M("World events", "Latest Minute was before Earliest Minute", W, "", "Nothing; it was set to Earliest."),
            M("World events", "has no steps, so it does nothing", W, "", "Add steps in its inspector."),
            M("World events", "(FireTrigger) has no trigger id; skipped", W, "", "Give the step a trigger id (the window's buttons make one with what it fires)."),
            M("World events", "(FireTrigger): nothing listens to", W, "Nothing in the scenes reacts to that id.", "Connect something to it, or fix the id."),
            M("World events", "(Notification) has no message; skipped", W, "", "Write the step's message."),
            M("World events", "has no Sound; skipped", W, "", "Pick the step's scene AudioSource."),
            M("World events", "has no Target; skipped", W, "", "Pick the step's scene object."),
            M("World events", "is in another scene; keep it in the event's scene", E, "The client finds a step's object in the event's own scene.", "Move the object or the event."),
            M("World events", "plays on awake, so it plays at raid start too", W, "", "Turn Play On Awake off on the AudioSource."),
            M("World events", "world event '", E, "A step's object must be in the build and unique by name among its siblings (the client finds it by its path).", "Move it out of EditorOnly objects; rename objects that share a name and place."),

            // scene setup
            M("Scene", "had no LocationScene, one was added", W, "Every scene needs one LocationScene; Build Map added it.", "Nothing to do; it's saved with the scene."),
            M("Scene", "LocationScene components, keep exactly one per scene", E, "The game reads one LocationScene per scene; several leave it reading the wrong one.", "Delete the extra LocationScene components."),
            M("Scene", "no LevelSettings in the active scene", W, "LevelSettings sets the map's fog, skybox and ambient and starts the airdrops.", "Generation > Essential > Map Skeleton In Active Scene adds one, or add LevelSettings to the first scene."),
            M("Scene", "LevelSettings had no fog mode", W, "Without a fog mode the game renders the map black; Build Map set Exponential with density 0.", "Nothing to do. Set the fog you want on LevelSettings."),
            M("Scene", "LevelSettings skybox removed", W, "With the full weather system the skybox would hide the TOD sky and clouds; Build Map removed it.", "Nothing to do."),
            M("Scene", "LevelSettings had no skybox", W, "Without a skybox the sky is black; Build Map set SDK_Skybox.", "Nothing to do, or set your own skybox material."),
            M("Lighting", "LevelSettings ambient mode is", W, "Skybox and Trilight ambient glow on every surface at night.", "Tarkov SDK > Quick Fixes > Fix Night Lighting."),
            M("Lighting", "lights the map day and night and makes it glow at night", W, "Unity's ambient light reaches every surface unshadowed, day and night.", "Tarkov SDK > Quick Fixes > Fix Night Lighting."),
            M("Scene", "had no EnvironmentManager, one was added", W, "The game looks for indoor triggers in the EnvironmentManager; Build Map added it.", "Nothing to do."),
            M("Birds", "birds have no flight paths", E, "The game's BirdsSpawner spawns nothing without paths (it logs 'bad initialization data').", "Add a path with the Bird Path Tool (Tools > Geometry), or delete the birds."),
            M("Birds", "bird flight path is missing or has no curve", E, "A bird flying it throws in the raid.", "Remove the empty entry from Paths (the Bird Path Tool's X), or draw it again."),
            M("Birds", "bird flight path isn't a loop", W, "Birds go from a path's end straight back to its start.", "Draw it again with the Bird Path Tool; its paths are always closed loops."),
            M("Birds", "bird flight path goes through something solid", W, "Birds fly straight through walls and hills.", "Bird Path Tool: drag the points marked in red up or away, or press Ground."),
            M("Birds", "birds are spawned under their spawner and take its scale", W, "A scaled spawner makes giant or tiny birds.", "Set the spawner's scale back to 1."),
            M("Birds", "bird Count is 0", W, "No birds fly.", "Set Count on the birds."),
            M("Birds", "hardly moves; a bird stops with fewer than", W, "A flock bird only moves with enough others close to it.", "Raise the flock's Count to 5 or more."),
            M("Birds", "flock Spawn Radius is wider than its Cohesion Radius", W, "Birds spawned far apart never find each other and stay still.", "Lower Spawn Radius below Cohesion Radius."),
            M("Birds", "the flock's centre is close to something solid", W, "Flock birds fly through it.", "Raise the Flock Centre object."),
            M("Birds", "birds have no bird prefab", E, "The game has nothing to spawn.", "Set Birds Prefabs to SDK_Crow or SDK_Gull (Assets/TarkovSDK/Birds)."),
            M("Birds", "a bird prefab is empty or has no Bird component", E, "The game throws when it spawns one.", "Use SDK_Crow or SDK_Gull, or add the game's Bird component to your prefab."),
            M("Birds", "needs its Animator and Carina set", E, "The game turns Carina and drives the Animator without checking them.", "Set both on the prefab's Bird component."),
            M("Scenes", "is open but not one of the Map Definition's scenes", W, "Only the Map Definition's scenes are built; what's in this one is missing in the raid.", "Add it to Scenes (Add To Map on the Map Definition), or close it if it isn't part of the map."),
            M("Scenes", "moved into the first scene", W, "A map has one of these, in its first scene; in another scene Build Map would have added a second.", "Nothing to do. Save the scenes."),
            M("Scenes", "and so does the first scene", W, "A map has one of these. A second EnvironmentManager switches itself off in the raid; two LevelSettings or time of day systems fight.", "Delete the one in the other scene."),
            M("Scenes", "shares an object with the scene's LocationScene", W, "Build Map can't move it into the first scene without the other scene's LocationScene.", "Move that component onto its own object in the first scene."),
            M("Scenes", "indoor trigger group(s) moved under the EnvironmentManager", W, "The game only checks the indoor triggers under the first scene's EnvironmentManager.", "Nothing to do. Save the scenes."),
            M("Scenes", "Part of the map has no navmesh under it", W, "The navmesh was baked without those scenes open (or before they were added), so bots can't walk there.", "Map Definition > Open All Scenes, then bake the navmesh again."),
            M("Lighting", "have no reflection cubemap and shine flat grey", W, "p0 materials without a Reflection Cubemap reflect flat grey, which no time of day changes.", "Tarkov SDK > Quick Fixes > Fix Shiny Materials."),
            M("Lighting", "has both the simple time of day rig and the full weather system", W, "Two time of day systems fight over the light.", "Generation > Time > Time of Day / Weather...: keep one."),
            M("Lighting", "had no time of day system, the simple one was added", W, "Without one the map renders black; Build Map added the simple rig.", "Nothing to do, or pick the full weather system in Time of Day / Weather."),
            M("Lighting", "have no cloud layer or texture", W, "The weather's cloud layer asset is gone (SDK versions before 1.9.2 replaced it whenever another weather system was built), so the raid has no clouds.", "Generation > Time > Time of Day / Weather..., build the full weather system again."),
            M("Lighting", "was missing and has been recreated", W, "The full weather system's sun light was deleted; Build Map remade it.", "Don't delete the 'Light' under the weather's components."),
            M("Lighting", "must be saved inactive (the game activates it)", W, "The full weather system is woken by the client once its shaders are the game's; Build Map switched it off.", "Nothing to do."),
            M("Lighting", "has lens flares but no flare settings", W, "Lens flares only draw through the map's flare settings.", "Tarkov SDK > Generation > Misc > Lens Flare Settings."),
            M("Lighting", "Entirely Indoors: the map has no solid colliders", W, "The whole-map indoor trigger is sized to the map's colliders, and there are none yet.", "Add geometry, or untick Entirely Indoors."),

            // audio
            M("Audio", "the ambient audio system is missing its data assets", W, "The game's ambient audio system lost its data assets.", "Remove it and add it again in Generation > Ambience > Ambient Sounds..."),
            M("Audio", "has no Outdoor Day clip", W, "The ambient audio system plays nothing by day.", "Set the Outdoor Day clip in Generation > Ambience > Ambient Sounds..."),
            M("Audio", "beds and the ambient audio system", W, "The simple beds play on top of the game's ambient system.", "Remove SDK_Ambience, or the ambient audio system."),
            M("Audio", "no rain cover, so rain falls on the screen indoors", W, "The full weather shows rain on the screen wherever there's no dry plane above the camera.", "Generation > Geometry > Rain Cover."),
            M("Audio", "room acoustics are set up but not baked", W, "Meta XR's room acoustics have no bake, so the raid has no Meta reverb.", "Generation > Audio > Spatial Audio..., Bake Acoustics."),
            M("Audio", "not set up for Meta XR Audio", W, "These sources play with plain panning and no room sound.", "Generation > Audio > Spatial Audio..., Fix Audio Sources (or Quick Fixes > Fix Audio Sources)."),
            M("Audio", "Fade Up Outdoors is too short for the game to fade", W, "The game doesn't fade under 0.5 s, so the outdoor ambience jumps to full on leaving a room.", "Spatial Audio window: Fade Smoothly."),
            M("Audio", "PA speaker is on an indoor or outdoor ambience mixer group", W, "The PA goes quiet on the other side of the indoor/outdoor line.", "Quick Fixes > Fix Broken Interactables (it moves speakers to @CommonSounds)."),
            M("Audio", "a sound has Pitch 0, which the game plays silent", W, "The game writes pitch 0 straight through: silence.", "Set the sound's Pitch to 1."),
            M("Audio", "the spatial audio system has no rooms", W, "Sounds play unoccluded.", "Generation > Audio > Spatial Audio..., Generate Rooms And Portals."),
            M("Audio", "too many audio rooms or portals", E, "The bake format can't hold that many.", "Merge small rooms, or remove some portals."),
            M("Audio", "no SpatialAudioCrossSceneGroup next to the SpatialAudioSystem", E, "The game only finds rooms under a cross-scene group.", "Remove the SpatialAudioSystem and create it again from the Spatial Audio window."),
            M("Audio", "audio room has no AudioTriggerArea", E, "The game can't tell when a player is in the room.", "Give the room an area (generate rooms again, or add an AudioTriggerArea child)."),
            M("Audio", "audio area without a BoxCollider", E, "An audio area is a trigger box.", "Add a BoxCollider to it."),
            M("Audio", "no outdoor audio room", W, "Outside every room the game's ambience treats the player as nowhere.", "Generate the rooms; the generator makes the outdoor room."),
            M("Audio", "audio portal outside the SpatialAudioSystem object", E, "The game loads every portal, but only finds rooms under the system, so the portal breaks the load.", "Move it under SpatialAudioSystem/Portals."),
            M("Audio", "audio room outside the SpatialAudioSystem object is ignored", W, "The game only finds rooms under the system.", "Move it under SpatialAudioSystem/Rooms."),
            M("Audio", "audio portal without a BoxCollider", E, "A portal is a box.", "Add a BoxCollider (1 x 1 x 0, scaled)."),
            M("Audio", "instead of 2; the raid fails to load", E, "The game reads both rooms of every portal at load, unchecked.", "Delete the portal or connect it to two rooms (generate the rooms again)."),
            M("Audio", "set to never rebake on build", W, "Rooms or portals changed but the routes weren't rebaked; the raid may fail to load them.", "Bake Sound Routes in the Spatial Audio window, or set Rebake On Build to Auto."),
            M("Audio", "no audio rooms are connected by portals", W, "Sound between rooms is only heard through walls.", "Generate portals (doors, windows, openings)."),
            M("Audio", "door portal with no door within 2 m", W, "A door portal opens and closes with the nearest door; none is close.", "Move the portal onto the door, or make it a Static (always open) portal."),
            M("Audio", "window portal with no breakable window within 2 m", W, "A window portal opens when its window breaks; none is close.", "Move it onto the window, or make it Static."),

            // geometry
            M("Geometry", "solid collider(s) on the Default layer", W, "Bullets and bots ignore colliders on Default.", "Select them, Tools > Geometry > Make Selected Static Geometry (Terrain layer), or split them into LowPoly + HighPoly."),
            M("Geometry", "without a BallisticCollider", W, "The game reads surface material and penetration from a BallisticCollider.", "Tools > Geometry > Add BallisticCollider To Selected Colliders."),
            M("Geometry", "renderer(s) use ProBuilder's own shaders", W, "ProBuilder's shaders don't exist in the game.", "Quick Fixes > Set Up ProBuilder For Tarkov."),
            M("Geometry", "ProBuilder object(s) have no collider", W, "Players and bullets go through them.", "Give them LowPoly + HighPoly colliders, or a Mesh Collider on the Terrain layer."),
            M("Geometry", "has no Terrain Data", E, "A terrain without data can't build.", "Assign its Terrain Data, or delete the terrain."),
            M("Geometry", "was set up for the game (Terrain layer, Terrain Collider)", W, "Build Map gave the terrain what the game needs.", "Nothing to do."),
            M("Geometry", "which the game doesn't load itself", W, "Its shader ships in the bundle as it is and may not render.", "Tools > Geometry > Terrain Surfaces > Set Up (SDK_Terrain)."),
            M("Geometry", "surfaces guessed from layer names", W, "Footsteps and bullets on those layers use a guessed surface.", "Tools > Geometry > Terrain Surfaces: pick each layer's surface."),
            M("Geometry", "has an empty terrain layer slot", W, "An empty slot paints nothing.", "Remove the empty layer from the terrain."),

            // interactables
            M("Interactables", "LootableContainer has no Template", W, "A container without a type can't be searched.", "Set its Template (Tools > Interactables > Loot Containers > Use On Selected Container)."),
            M("Interactables", "is not a 24-character item id, so it can't be searched", W, "Template must be an item template id.", "Pick a type in Loot Containers."),
            M("Interactables", "breakable window can't break", W, "The game can't work out the glass's plane or crack pattern.", "The glass must be thin along one axis, have UVs, a MeshCollider and a Glass BallisticCollider on TransparentCollider."),
            M("Interactables", "HandlerInteractable has no Interactable", W, "Its trigger does nothing.", "Set its Interactable, or connect it again in Triggers & Sounds."),
            M("Interactables", "lamp group switches nothing", W, "The group has nothing to switch.", "Press Fill in its inspector."),
            M("Interactables", "nothing switches this lamp group", W, "Only HandlerLamp switches groups, and none points at it.", "Add Light Switch in its inspector, or Triggers & Sounds."),
            M("Interactables", "keycard door without a card reader", E, "KeycardDoor reads its first reader unchecked; unlocking throws.", "Add the reader back (GameObject > Tarkov SDK > Interactables > Keycard Door has one)."),
            M("Interactables", "keycard door has no KeyId", W, "No keycard opens it.", "Set Key Id to the keycard's item id."),
            M("Interactables", "card reader has no Link", E, "The reader's Link is its door; the game throws without it.", "Set the reader's Link."),
            M("Interactables", "needs both its Status Light and Flicker Light", E, "The reader's Start needs both lights.", "Set both lights on the reader."),
            M("Interactables", "card reader needs a collider on the Interactive layer", W, "Players can't look at it to use it.", "Give the reader's own object a collider on the Interactive layer."),
            M("Interactables", "InteractiveSubscriber has no Subscribee", E, "The game throws on it at raid start.", "Set its Subscribee, or delete it."),
            M("Interactables", "animated object", W, "An animated object needs an Animator, and something to open it.", "Check its SDK_Animation child; connect a switch or zone in Triggers & Sounds."),

            // triggers
            M("Triggers", "TriggerZone needs a trigger collider", W, "Players never set it off.", "Add a trigger collider to its own object."),
            M("Triggers", "TriggerZone is not on the Triggers layer", W, "Players may not set it off.", "Put it on the Triggers layer (Fix Broken Interactables does)."),
            M("Triggers", "TriggerZone has no trigger id", W, "It sends nothing.", "Connect it in Triggers & Sounds."),
            M("Triggers", "but nothing sends it", W, "A listener waits for an id that's never sent. An object's Id isn't a trigger id.", "Connect a zone or switch to it in Triggers & Sounds."),
            M("Triggers", "but nothing listens to it", W, "An id is sent but nothing reacts.", "Connect something to it in Triggers & Sounds, or remove the id."),

            // spawns, quest zones, loot
            M("Spawns", "spawn point has no Sides", W, "Nobody can use it.", "Set its Sides (PMC sides, Savage)."),
            M("Spawns", "spawn point has no Categories", W, "Nobody can use it.", "Set its Categories (Player, Bot, Boss...)."),
            M("Spawns", "PMC player spawn has no Infiltration", W, "PMCs need an entry point name; extracts use it.", "Set Infiltration on the spawn point."),
            M("Loot", "loot item(s) without a valid 24-character template id are skipped", W, "Some items aren't item ids.", "Pick the items again with the inspector's search."),
            M("Loot", "loot spawn has no items", W, "Nothing can spawn there.", "Add items in its inspector."),
            M("Quests", "quest zone has no Id", W, "Quests find zones by Id.", "Set its Id (Copy Id gives it to your quest)."),
            M("Quests", "has the Id", W, "Two quest zones of a kind share an Id.", "Give each its own Id."),
            M("Quests", "quest zone has no trigger collider", W, "Players are never in it.", "Give it a trigger collider."),
            M("Hazards", "barbed wire has no trigger collider", W, "It never catches anyone.", "Give it a trigger box."),

            // transits and extracts
            M("Extracts", "TransitPoint has no parameters", E, "A transit needs its parameters.", "Make it again with Exfils > Transit Point, or in the Extracts window."),
            M("Extracts", "duplicate transit id", E, "Every transit on the map needs its own id.", "Change one transit's id."),
            M("Extracts", "transit has no destination", W, "It goes nowhere.", "Set parameters.location (the Extracts window lists the maps)."),
            M("Extracts", "transit needs a trigger collider", E, "Players can't enter it.", "Add a trigger collider to the transit's object."),
            M("Extracts", "IndoorTrigger is not under a TriggerGroup", W, "The game never checks it.", "Make indoor triggers with GameObject > Tarkov SDK > Environment > Indoor Trigger."),
            M("Extracts", "duplicate exfil name", E, "The game reads an extract's rules by name.", "Rename one in the Extracts window."),
            M("Extracts", "exfil has no EntryPoints", W, "No PMC can use it.", "Tick entry points in the Extracts window."),
            M("Extracts", "exfil needs a trigger collider", E, "Players can't enter it.", "Add a trigger collider to the exfil's object."),
            M("Extracts", "car extraction needs its car (WheelDrive) and spline", E, "The car has nothing to drive or nowhere to go.", "Make the car extract again in the Extracts window."),
            M("Extracts", "the car isn't under its CarExtraction", W, "The car stays when the extract is off.", "Move the car under its CarExtraction."),
            M("Extracts", "WheelDrive needs its two front wheels", E, "The car's Start reads them.", "Set the front wheels (objects with 'wheel' in their names)."),
            M("Extracts", "every WheelCollider needs its wheel model as its first child", E, "WheelDrive spins the first child.", "Put each wheel model under its WheelCollider."),
            M("Extracts", "WheelDrive needs its Ballistics object", E, "It moves it every frame.", "Set Ballistics."),
            M("Extracts", "WheelDrive needs its RigidbodySpawner", E, "", "Set its RigidbodySpawner."),
            M("Extracts", "WheelDrive needs an AudioSource", E, "The car extraction plays it.", "Add an AudioSource to the car."),
            M("Extracts", "car extraction follows no extract", W, "The car just stays parked.", "Link it to its extract (the Extracts window does)."),

            // hazards
            M("Hazards", "needs a BoxCollider on its object", E, "A border zone is a box.", "Add a BoxCollider."),
            M("Hazards", "its box is off-centre", W, "The game measures the zone from its transform.", "Move the object instead of the box centre."),
            M("Hazards", "sniper zone has no SniperImitator", W, "Its shots are silent.", "Make the zone again, or add the imitator."),
            M("Hazards", "SniperImitator needs its sniper zone", E, "It throws on the first shot.", "Set its zone and at least one sniper position."),
            M("Hazards", "flare zone trigger handler has no collider", E, "", "Give the handler its trigger box."),
            M("Hazards", "flare zone has no PhysicsTriggerHandler", E, "Firing a flare there does nothing.", "Make the flare zone again."),
            M("Hazards", "claymore has no tripwires", W, "Nothing sets it off.", "Keep its MineDirectionalColliders with trigger boxes."),
            M("Airdrops", "LevelPhysicsSettings in the map's scenes", W, "The game uses the first and logs an error for the others.", "Keep one."),

            // raid events
            M("Raid events", "forced transit '", E, "A forced transit needs its transit and a way to fire it.", "Set its Transit; give a hidden one a trigger id."),
            M("Raid events", "neither forced on entering nor by a trigger", W, "It's an ordinary transit.", "Tick On Enter or give it a trigger id."),
            M("Raid events", "runs neither at raid start nor on a trigger", W, "The gear strip never runs.", "Tick On Raid Start or give it a trigger id."),
            M("Raid events", "moves gear to a container but has none", E, "", "Set its Container (the stash made with it)."),
            M("Raid events", "its container", W, "The stash needs an Id and an empty container type big enough for a kit.", "Use the Gear Stash type (Loot Containers), 10 x 16."),
            M("Raid events", "notifies the player but has no message", W, "", "Write the notification's message."),
            M("Raid events", "notification '", W, "A notification needs a trigger id and a message.", "Connect it in Triggers & Sounds; write its message."),
            M("Raid events", "caption '", W, "A caption needs a trigger id and lines.", "Connect it; add lines."),
            M("Raid events", "health change '", W, "A health change needs a trigger id and body parts; a repeat needs a stop id or it runs until death.", "Connect it in Triggers & Sounds (Stop When They Leave for zones)."),
            M("Raid events", "effect '", W, "An effect needs a trigger id and effects; fractures only work on arms and legs.", "Connect it; check its effects."),

            // BTR
            M("BTR", "BTR routes (MapPathConfig); the game uses one per map", E, "", "Keep one BTR route object."),
            M("BTR", "the BTR has no active route", E, "One active route is picked each raid.", "Tick Active on a route."),
            M("BTR", "which doesn't exist", E, "A route names a stop that was deleted.", "Pick existing stops in the route."),
            M("BTR", "among its stops too, so the taxi offers it", W, "The taxi offers every listed stop.", "Keep Comes From / Leaves To out of Stops In Order."),
            M("BTR", "more than once; the game drives each stop once per pass", W, "The game keys a route's parts by stop.", "List each stop once."),
            M("BTR", "no BTR road for", E, "It would stop dead there.", "BTR Path Editor > Roads > Generate Missing."),
            M("BTR", "have no three-point turn", E, "It slides sideways when its next road starts behind it.", "BTR Path Editor > Roads > Generate Missing (or Remake Turns on the stop)."),
            M("BTR", "which doesn't face its road", E, "The BTR appears facing its enter stop and drives off without a turn.", "Regenerate All Roads (it turns enter points to their road)."),
            M("BTR", "(BTR chance and timing); defaults are used", W, "", "Make the route again, or add the settings in the BTR Path Editor."),
            M("BTR", "drives once through but leaves to its own enter point", W, "The game keys a route's parts by stop, so it never drives back to the enter point and the BTR stays at its last stop.", "Give the route its own Leaves To stop."),
            M("BTR", "makes a three-point turn at", W, "At those stops the next road starts behind the BTR, so it turns round (a route that doubles back, or a road leaving the wrong way).", "BTR Path Editor > Roads > Regenerate All with Drive-through on; Preview shows where it happens."),
            M("BTR", "slides sideways for", W, "That road turns 90 degrees or more under the BTR, so it keeps its heading and slides.", "Smooth the road in the BTR Path Editor (fewer sharp corners), or rebuild it drive-through."),
            M("BTR", "no road from", W, "The game throws there and the BTR stops for good.", "Generate the missing road."),
            M("BTR", "stays at", W, "The route has nothing after that stop.", "Check the route's Drives setting and its Leaves To stop."),
            M("BTR", "turn is missing (the game throws)", W, "The next road starts behind the BTR and the stop has no turn for the way it faces.", "Remake the stop's turns (BTR Path Editor > Roads, select the stop)."),
            M("BTR", "the route's enter point", W, "The route's Comes From stop was deleted.", "Pick an existing stop as Comes From."),
            M("BTR", "still driving after an hour", W, "The route never reaches its exit in the check (a loop with no way out).", "Check the route's laps and its Leaves To stop."),
            M("BTR", "the route has no stops", W, "A route needs stops in order to drive.", "Add stops to the route (Stops & Routes tab)."),
            M("BTR", "has no curve", W, "A road has fewer than two points.", "Rebuild the road in the BTR Path Editor."),
            M("BTR", "drove to a stop that doesn't exist", W, "A road leads to a deleted stop.", "Regenerate the roads."),

            // mortar
            M("Mortar", "mortar zones or batteries without the SDK_Mortar settings", E, "Zones and batteries belong under the map's SDK_Mortar.", "Make them with GameObject > Tarkov SDK > World Events > Mortar Zone."),
            M("Mortar", "more than one SDK_Mortar in the map", E, "The game has one mortar setup per map.", "Keep one, with every zone and battery under it."),
            M("Mortar", "has no zones", E, "", "Add a zone (World Events > Mortar Zone)."),
            M("Mortar", "has no batteries", E, "Shells fly from a battery.", "Add one (World Events > Mortar Battery), a few hundred metres off the map."),
            M("Mortar", "mortar battery has", E, "The game reads a battery's second gun unchecked and never fires its last one.", "Give it at least two guns (child objects), three like retail."),
            M("Mortar", "mortar zone has no grid points", E, "", "Set Points above 0."),
            M("Mortar", "Points Per Shelling's minimum is above its maximum", W, "", "Swap them."),
            M("Mortar", "every mortar battery is over 10 km away", E, "The game then looks for a battery with id 0 and throws.", "Move a battery closer."),
            M("Mortar", "have no ground under them", W, "The game drops shells from 300 m straight down; with nothing there they land at the world origin.", "Move or shrink the zone; Preview shows the points."),
            M("Mortar", "planned mortar shelling is on but no zone is Planned and Active", W, "It never fires.", "Tick Planned and Active on a zone, or turn planned shelling off."),
            M("Mortar", "Called Strike Templates; the game only uses the first", W, "", "Keep one called strike template."),
            M("Mortar", "is shorter than the warning time", W, "The warning comes late.", "Raise the zone's Trigger Delay above the Warning Time."),

            // triggered spawns
            M("Spawns", "bot spawn '", W, "A bot spawn needs a trigger id, and points on the navmesh.", "Connect it in Triggers & Sounds; move its points onto the navmesh."),
            M("Spawns", "loot spawn '", W, "A triggered loot spawn needs a trigger id and valid items.", "Connect it; pick items in its inspector."),
            M("Spawns", "prefab spawn '", W, "A prefab spawn needs a trigger id and a Prefab; Show and Hide need a scene object outside EditorOnly objects in the same scene, with a unique name among its siblings.", "Connect it; pick its Prefab; rename clashing objects."),
        };
    }
}
