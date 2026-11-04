using System;
using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // A building's plan, for the Building Builder (Tarkov SDK > Tools > Geometry > Building Builder...): storeys of
    // cells painted with rooms, openings (doors, windows, gaps) on the edges between cells, stairs, a roof. The builder
    // makes the building from it under the building's Generated child: merged meshes (with a cheaper outside-only LOD),
    // mesh colliders for movement and bullets, the doors, windows, lamps, stairs and furniture, holes (with rubble or
    // breakable chunks), and the lighting and weather volumes the game needs (darkness volumes and ambient portals,
    // rain cover, indoor triggers).
    // Editor-only data on an EditorOnly object (SDK_BuildingPlan, a child of the building).
    [AddComponentMenu("Tarkov SDK/Building Plan")]
    public class TarkovBuilding : MonoBehaviour
    {
        public enum OpeningKind { Door, Window, Gap }
        public enum RoofKind { Flat, Gable }
        public enum Surface { Concrete, Brick, Plaster, Wood, Metal, Tile }
        // a room's surface: Auto = from its material's name (wood, metal, tile, brick, plaster, concrete), else the building's
        public enum SurfaceChoice { Auto, Concrete, Brick, Plaster, Wood, Metal, Tile }
        public enum DoorState { Shut, Open, Locked }
        // what a hole's material does: nothing (an open hole), loose rubble on the floor, or a piece that stays in the
        // wall until an explosion (or a bullet) breaks it into chunks
        public enum DebrisKind { None, Rubble, Breakable }
        public enum BevelKind { Round, Chamfer }
        public enum TrimSide { Inside, Outside }
        public enum TrimAnchor { Floor, Ceiling, Top }
        public enum TrimStoreys { All, Ground, Upper, Top, NotTop }

        [Serializable]
        public class Room
        {
            public string Name = "Room";
            public Color Color = new Color(0.4f, 0.7f, 1f);
            [Tooltip("Empty: the building's.")]
            public Material Floor, Walls, Ceiling;
            [Tooltip("A ceiling lamp in the middle of each part of the room.")]
            public bool Lamp = true;
            [Tooltip("This room's lamps use this prefab (empty: the building's Lamp Prefab, else the SDK's lamp). Its pivot goes 5 cm under the ceiling.")]
            public GameObject LampPrefab;
            [Tooltip("Turns this room's lamp prefab (degrees), e.g. 90 about x for a lamp modelled Z-up (lying on its side).")]
            public Vector3 LampRotation;
            [Tooltip("What bullets hit (and footsteps sound like) on this room's floor. Auto: from the floor material's name, else the building's.")]
            public SurfaceChoice FloorSurface;
            [Tooltip("What bullets hit on this room's walls. Auto: from the wall material's name, else the building's.")]
            public SurfaceChoice WallSurface;
            [Tooltip("Colours this room's lamps.")]
            public bool LampTint;
            public Color LampColor = new Color(1f, 0.92f, 0.8f);
            [Tooltip("Multiplies its lamps' brightness.")]
            [Min(0f)] public float LampIntensity = 1f;
            [Tooltip("Its lamps' reach in metres (0: the building's Lamp Range for the SDK's lamp, the prefab's own for a prefab).")]
            [Min(0f)] public float LampRange;
        }

        // a door's settings, put on the door (the SDK's or its prefab's) after it's placed
        [Serializable]
        public class DoorSettings
        {
            [Tooltip("Off: the door as it is (the SDK's door starts shut, unlocked, breachable; a prefab as it was made).")]
            public bool Override;
            public DoorState State = DoorState.Shut;
            [Tooltip("Locked doors: the key's item id (empty: no key; it can only be breached).")]
            public string KeyId = "";
            public bool CanBeBreached = true;
            [Tooltip("How far it swings open, in degrees.")]
            [Range(30f, 180f)] public float OpenAngle = 95f;
            [Tooltip("Off: players can't open or close it.")]
            public bool Interactable = true;
        }

        [Serializable]
        public class Storey
        {
            [Tooltip("Floor to floor, in metres (the slab included).")]
            public float Height = 3f;
            // Width x Depth room numbers, x fastest: 0 = no room, n = Rooms[n - 1]
            public int[] Cells = new int[0];
            [Tooltip("The outside walls of this storey (empty: the building's).")]
            public Material Exterior;
            // cells with no ceiling: the floor above is open there (a double-height hall, a light well), or the roof is
            // (a courtyard) when nothing is above
            public List<Vector2Int> OpenCeiling = new List<Vector2Int>();
        }

        // A rounded (or cut) room corner at a grid vertex: the walls either side are replaced by an arc from Radius cells
        // along one to Radius cells along the other, and the floor, roof and volumes follow it. Quadrant: which cell at
        // the corner is the room's, 0 (+x, +z), 1 (-x, +z), 2 (-x, -z), 3 (+x, -z). The two cells beside it must be one
        // room (or none: the outside), which gets the corner cut off.
        [Serializable]
        public class Bevel
        {
            public int Storey;
            public int X, Z;
            public int Quadrant;
            public BevelKind Kind = BevelKind.Round;
            [Tooltip("In cells, from the corner along each wall (fractions allowed); half the room's side rounds that side off completely.")]
            [Min(0.25f)] public float Radius = 2f;
            [Tooltip("Straight pieces the arc is made of.")]
            [Range(2, 24)] public int Segments = 8;
        }

        // a material on one face of the wall on an edge (as Opening's edges): the +side (room B's) or the -side
        [Serializable]
        public class WallPaint
        {
            public int Storey;
            public int X, Z;
            public bool AlongZ;
            public bool Plus;
            public Material Material;
        }

        // A moulding run along the walls: its profile (a cross-section, x out from the wall, y up, metres, from its
        // bottom on the wall round to its top) swept along every room's walls (Inside) or the building's outside
        // (Outside), mitred at corners, following bevels, stopping at the openings it would cross.
        [Serializable]
        public class Trim
        {
            public string Name = "Trim";
            public bool Enabled = true;
            public TrimSide Side = TrimSide.Inside;
            [Tooltip("Floor: up from the storey's floor. Ceiling: its top under the ceiling. Top: up from the next floor's level (the roof's top on the top storey).")]
            public TrimAnchor Anchor = TrimAnchor.Floor;
            [Tooltip("Moves it up (or down) from the anchor, in metres.")]
            public float Height;
            public TrimStoreys Storeys = TrimStoreys.All;
            [Tooltip("Inside trims: only in this room (0: every room).")]
            public int Room;
            [Tooltip("Empty: the building's trim material.")]
            public Material Material;
            public Vector2[] Profile = new Vector2[0];
        }

        // how a material is laid on the building: metres per repeat of its texture, offset, turn; and, optionally,
        // what bullets hit where it's used on walls and floors
        [Serializable]
        public class MaterialOption
        {
            public Material Material;
            public Vector2 Tile = Vector2.one;
            public Vector2 Offset;
            [Range(-180f, 180f)] public float Rotation;
            public bool OverrideSurface;
            public Surface Surface = Surface.Concrete;
        }

        // on the edge between two cells: along x at z (between rows z - 1 and z) when !AlongZ, along z at x (between
        // columns x - 1 and x) when AlongZ; Span cells long from (X, Z)
        [Serializable]
        public class Opening
        {
            public int Storey;
            public int X, Z;
            public bool AlongZ;
            public int Span = 1;
            public OpeningKind Kind = OpeningKind.Door;
            public float Width = 1.1f;
            public float Height = 2.1f;
            [Tooltip("A window's bottom above the floor.")]
            public float Sill = 0.9f;
            [Tooltip("Along the edge from its middle, in metres.")]
            public float Offset;
            [Tooltip("A door or window prefab to place in it instead of the SDK's (its pivot at the bottom middle of the opening, facing along the wall's normal).")]
            public GameObject Prefab;
            [Tooltip("Doors: the hinge on the other side.")]
            public bool HingeRight;
            [Tooltip("Doors: it opens the other way.")]
            public bool ReverseSwing;
            [Header("Prefab")]
            [Tooltip("Turn the prefab square to the wall and size the hole to its frame, from its meshes. Off: its pivot on the opening's bottom middle, facing out of the wall, the hole the plan's size.")]
            public bool AutoFit = true;
            [Tooltip("Moves the prefab after it's placed: x along the wall, y up, z out of the wall (metres). Dragged by the Select tool's handles.")]
            public Vector3 PrefabOffset;
            [Tooltip("Turns the prefab about the vertical after it's placed (degrees).")]
            public float PrefabTurn;
            [Tooltip("Scales the prefab (1: as it is). The hole follows when Auto Fit is on.")]
            public float PrefabScale = 1f;
            public DoorSettings Door = new DoorSettings();
        }

        // a straight flight from cell (X, Z) along Direction (0 +z, 1 +x, 2 -z, 3 -x), Length cells long, Width cells
        // wide, up to the next storey (whose floor is open above it)
        [Serializable]
        public class Stair
        {
            public int Storey;
            public int X, Z;
            public int Direction;
            public int Length = 4;
            [Tooltip("Cells across; bots need two (1 m cells) to get up past the edges of the floor above.")]
            public int Width = 2;
            [Tooltip("A stair model to use instead of the SDK's steps (empty: the building's Default Stair, else the SDK's). Which way it climbs is found from its mesh. The SDK's ramp stays for walking on.")]
            public GameObject Prefab;
            [Tooltip("Stretch the prefab to the flight (its run, rise and width). Off: as it is, its bottom front edge on the flight's start.")]
            public bool FitToFlight = true;
            [Tooltip("Quarter turns, when the prefab was found climbing the wrong way.")]
            [Range(0, 3)] public int PrefabTurn;
        }

        // a prefab placed in the building (furniture): on a storey, at a place on the plan, turned about the vertical
        [Serializable]
        public class Prop
        {
            public GameObject Prefab;
            public int Storey;
            [Tooltip("In the building's space, metres: x and z on the plan, y up from the storey's floor.")]
            public Vector3 Position;
            [Tooltip("Turn about the vertical, in degrees.")]
            public float Yaw;
            [Min(0.01f)] public float Scale = 1f;
        }

        // a box cut out of the building (walls, floors, roofs; not the curved walls' or trims' shapes, whose faces in it
        // are dropped), in the building's space; rough edges are more boxes round its rim. Its material can be left as
        // rubble or as a piece that breaks into chunks (physics props) when a grenade goes off near it.
        [Serializable]
        public class Hole
        {
            [Tooltip("Its middle in the building's space (metres).")]
            public Vector3 Center;
            [Tooltip("Its size along the building's x, y (up) and z.")]
            public Vector3 Size = new Vector3(1.4f, 1.4f, 0.8f);
            [Tooltip("0: a clean box. Higher: ragged edges and gouges round its rim (it stays within its size).")]
            [Range(0f, 1f)] public float Rough = 0.5f;
            public int Seed;
            public DebrisKind Debris = DebrisKind.None;
            [Tooltip("The chunks' size, in metres.")]
            [Range(0.15f, 1f)] public float ChunkSize = 0.35f;
            [Tooltip("Breakable: a bullet hit breaks it too (else only explosions).")]
            public bool BreaksWhenShot;
            [Tooltip("Breakable: explosions this many metres from its middle break it.")]
            [Range(1f, 20f)] public float BlastRadius = 6f;
        }

        [Header("Grid")]
        [Min(1)] public int Width = 10;
        [Min(1)] public int Depth = 8;
        [Tooltip("The cell size in metres.")]
        [Min(0.25f)] public float CellSize = 1f;

        [Header("Structure")]
        public float WallThickness = 0.25f;
        public float InnerWallThickness = 0.12f;
        public float SlabThickness = 0.25f;
        [Tooltip("How far the ground floor stands above the building's pivot.")]
        public float Plinth = 0.3f;
        public RoofKind Roof = RoofKind.Flat;
        [Range(10f, 50f)] public float RoofPitch = 30f;
        public float Overhang = 0.3f;
        [Tooltip("Flat roofs: the low wall round the edge.")]
        public float Parapet = 0.5f;

        [Header("Materials")]
        public Material ExteriorMaterial;
        public Material InteriorMaterial;
        public Material FloorMaterial;
        public Material CeilingMaterial;
        public Material RoofMaterial;
        public Material TrimMaterial;
        [Tooltip("What bullets hit (and sound like) on the outer walls.")]
        public Surface ExteriorSurface = Surface.Brick;
        public Surface InteriorSurface = Surface.Plaster;
        public Surface FloorSurface = Surface.Concrete;

        [Header("Fittings")]
        [Tooltip("Doors without their own prefab use this (else the SDK's door).")]
        public GameObject DefaultDoor;
        [Tooltip("Windows without their own prefab use this (else the SDK's breakable glass).")]
        public GameObject DefaultWindow;
        public bool Glass = true;
        [Tooltip("Stairs without their own prefab use this (else the SDK's steps).")]
        public GameObject DefaultStair;
        [Tooltip("Lamps use this prefab (else the SDK's lamp).")]
        public GameObject LampPrefab;
        public float LampRange = 7f;
        public bool Frames = true;

        [Header("Generate")]
        public bool DarknessVolumes = true;
        public bool AmbientPortals = true;
        public bool RainCover = true;
        public bool IndoorTriggers = true;
        [Tooltip("A cheaper outside-only mesh at a distance, and culled when small on screen.")]
        public bool Lods = true;

        public List<Storey> Storeys = new List<Storey>();
        public List<Room> Rooms = new List<Room>();
        public List<Opening> Openings = new List<Opening>();
        public List<Stair> Stairs = new List<Stair>();
        public List<Bevel> Bevels = new List<Bevel>();
        public List<WallPaint> Paints = new List<WallPaint>();
        public List<Trim> Trims = new List<Trim>();
        public List<MaterialOption> MaterialOptions = new List<MaterialOption>();
        public List<Prop> Props = new List<Prop>();
        public List<Hole> Holes = new List<Hole>();

        // what was last built: the whole plan's hash, and each storey's (then the roof's); the builder rebuilds the groups
        // whose hash differs
        [HideInInspector] public string BuiltHash;
        [HideInInspector] public List<string> BuiltGroups = new List<string>();

        public int Get(int storey, int x, int z)
        {
            if (storey < 0 || storey >= Storeys.Count || x < 0 || z < 0 || x >= Width || z >= Depth)
                return 0;
            var cells = Storeys[storey].Cells;
            int i = z * Width + x;
            return cells != null && i < cells.Length ? cells[i] : 0;
        }

        public void Set(int storey, int x, int z, int room)
        {
            if (storey < 0 || storey >= Storeys.Count || x < 0 || z < 0 || x >= Width || z >= Depth)
                return;
            Fit(Storeys[storey]);
            Storeys[storey].Cells[z * Width + x] = room;
        }

        // the storey's cells at the grid's size (kept where they were when the grid grows or shrinks)
        public void Fit(Storey storey, int oldWidth = -1)
        {
            if (oldWidth < 0)
                oldWidth = Width;
            if (storey.Cells != null && storey.Cells.Length == Width * Depth && oldWidth == Width)
                return;
            var old = storey.Cells ?? new int[0];
            var cells = new int[Width * Depth];
            int oldDepth = oldWidth > 0 ? old.Length / oldWidth : 0;
            for (int z = 0; z < Mathf.Min(Depth, oldDepth); z++)
                for (int x = 0; x < Mathf.Min(Width, oldWidth); x++)
                    cells[z * Width + x] = old[z * oldWidth + x];
            storey.Cells = cells;
        }

        // the cells of a storey with no ceiling (the Ceiling tool's)
        public HashSet<Vector2Int> OpenCeilings(int storey)
        {
            var cells = new HashSet<Vector2Int>();
            if (storey >= 0 && storey < Storeys.Count && Storeys[storey].OpenCeiling != null)
                foreach (var c in Storeys[storey].OpenCeiling)
                    if (c.x >= 0 && c.y >= 0 && c.x < Width && c.y < Depth)
                        cells.Add(c);
            return cells;
        }

        // the floor's height of a storey above the pivot (the top of its slab)
        public float Base(int storey)
        {
            float y = Plinth;
            for (int i = 0; i < storey && i < Storeys.Count; i++)
                y += Storeys[i].Height;
            return y;
        }
    }
}
