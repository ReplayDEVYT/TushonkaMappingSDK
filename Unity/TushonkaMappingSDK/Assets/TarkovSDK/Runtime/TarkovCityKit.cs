using System;
using System.Collections.Generic;
using UnityEngine;

namespace TarkovSDK
{
    // What the City Builder (Tarkov SDK > Tools > Geometry > City Builder...) builds a city from: the ground's materials
    // and the prefabs it places along the streets and on the blocks. Any material or prefab works: your own, the Prefab
    // Library's, or Tarkov's own through the Asset Importer (its Add To City Kit button imports and adds them here).
    // Empty slots get the SDK's plain generated materials; a kit without buildings gets placeholder blocks.
    // Editor-only data: nothing in the map references it.
    public class TarkovCityKit : ScriptableObject
    {
        // which way a prefab's front (its door side, a car's bonnet) points in the prefab, round Y
        public enum Facing
        {
            [InspectorName("+Z (forward)")] PositiveZ,
            [InspectorName("+X (right)")] PositiveX,
            [InspectorName("-Z (back)")] NegativeZ,
            [InspectorName("-X (left)")] NegativeX,
        }

        [Serializable]
        public class Piece
        {
            public GameObject Prefab;

            [Tooltip("How often this one is picked, against the others in its list.")]
            [Min(0f)]
            public float Weight = 1f;

            [Tooltip("Which way the prefab's front points: buildings face the street with it, cars drive with it, lamps lean over the road with it.")]
            public Facing Front;

            [Tooltip("Metres up (or down, below zero) from the ground it stands on; for buildings modelled with their foundations below the pivot.")]
            public float Height;
        }

        [Header("Ground")]
        [Tooltip("The roads (and the ground under everything). Bullets and footsteps: asphalt.")]
        public Material Road;

        [Tooltip("The sidewalks round each block. Bullets and footsteps: concrete.")]
        public Material Sidewalk;

        [Tooltip("The kerb's side, between the road and the sidewalk.")]
        public Material Curb;

        [Tooltip("The ground of a block between its buildings. Bullets and footsteps by its name (grass, gravel, dirt, tile ... else concrete).")]
        public Material Lot;

        [Tooltip("The ground of park blocks. Bullets and footsteps by its name, grass if it doesn't say.")]
        public Material Park;

        [Tooltip("Lane lines and pedestrian crossings.")]
        public Material Markings;

        [Tooltip("The placeholder buildings' material, when the kit has no buildings.")]
        public Material PlaceholderBuilding;

        [Tooltip("Metres of ground one repeat of a ground texture covers.")]
        [Min(0.25f)]
        public float TextureMetres = 4f;

        [Header("Prefabs")]
        [Tooltip("Placed side by side along each block's streets, fronts to the street.")]
        public List<Piece> Buildings = new List<Piece>();

        [Tooltip("Street lamps, at the kerb, fronts over the road.")]
        public List<Piece> Lamps = new List<Piece>();

        [Tooltip("Benches, bins, hydrants, signs: on the sidewalks, near the buildings, fronts to the street.")]
        public List<Piece> StreetProps = new List<Piece>();

        [Tooltip("Parked along the kerbs, driving on the right.")]
        public List<Piece> Vehicles = new List<Piece>();

        [Tooltip("Street trees on the sidewalks and the trees of parks.")]
        public List<Piece> Trees = new List<Piece>();

        [Tooltip("Benches, playground pieces, fountains: scattered in parks.")]
        public List<Piece> ParkProps = new List<Piece>();
    }
}
