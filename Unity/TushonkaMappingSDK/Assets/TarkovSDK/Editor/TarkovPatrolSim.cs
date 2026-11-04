using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Random = System.Random;

namespace TarkovSDK.Editor
{
    // Bots patrolling a zone in the editor, by the game's choices with scav (assault) settings:
    //  - the next point: PatrolPointChooserBasic.FindNextPoint, a random point of the bot's way that no other bot owns
    //    and that is over 2 m away (NEXT_POINT_DIST_SQRT 4); the bot owns it until it picks another;
    //  - cut paths: on patrolling ways, CHANCE_TO_CUT_WAY 75% of the time it walks only CUT_WAY_MIN..MAX (40-65%) of the
    //    path before choosing again (PathDistCoef);
    //  - at the point: a stayPoint keeps it about LOOK_TIME_BASE (12 s), a checkPoint a short look round (3 s here); on
    //    a reserved way it stays RESERVE_TIME_STAY (72 s);
    //  - ways: every CHANGE_WAY_TIME (125 s) x 0.6-1.4 it moves to another way of the zone half the time (TryToFindWay:
    //    CHANCE_TO_CHANGE_WAY 50 says when it doesn't), boss ways excluded, reserved ones allowed (CAN_CHOOSE_RESERV).
    // Movement is a walk at 1.6 m/s along the navmesh path: a picture of where bots go and how they spread, not the
    // game's steering, looking or reactions.
    public sealed class TarkovPatrolSim
    {
        private const float WalkSpeed = 1.6f;
        private const float StayTime = 12f;
        private const float CheckTime = 3f;
        private const float ReserveStayTime = 72f;
        private const float ChangeWayTime = 125.1f;

        public sealed class Bot
        {
            public int Index;
            public Vector3 Position;
            public PatrolWay Way;
            public PatrolPoint Target;
            public Vector3[] Path;
            public int Corner;
            public float Walked, StopAfter;
            public float Wait;
            public float NextWayChange;
            public string State = "starting";
            public readonly List<Vector3> Trail = new List<Vector3>();
        }

        public readonly List<Bot> Bots = new List<Bot>();
        public readonly Dictionary<PatrolPoint, int> Visits = new Dictionary<PatrolPoint, int>();
        public float Time;
        public int WayChanges;
        private readonly BotZone _zone;
        private readonly Random _random;
        private readonly Dictionary<PatrolPoint, Bot> _owners = new Dictionary<PatrolPoint, Bot>();
        private readonly NavMeshPath _path = new NavMeshPath();

        public TarkovPatrolSim(BotZone zone, int bots, int seed)
        {
            _zone = zone;
            _random = new Random(seed);
            var ways = Ways(false);
            if (ways.Count == 0)
                return;
            var spawns = zone.GetComponentsInChildren<EFT.Game.Spawning.SpawnPointMarker>(true).Select(m => m.transform.position)
                .Concat(zone.SpawnPointMarkers?.Where(m => m != null).Select(m => m.transform.position) ?? Enumerable.Empty<Vector3>()).ToList();
            if (spawns.Count == 0)
                spawns.Add(zone.transform.position);
            for (int i = 0; i < bots; i++)
            {
                var start = spawns[i % spawns.Count];
                if (NavMesh.SamplePosition(start, out var hit, 5f, NavMesh.AllAreas))
                    start = hit.position;
                var bot = new Bot
                {
                    Index = i,
                    Position = start,
                    // ChooseStartWay: a random way that isn't reserved (TRY_CHOOSE_RESERV_WAY_ON_START is off for scavs)
                    Way = PickWay(ways.Where(w => w.PatrolType != PatrolType.reserved).DefaultIfEmpty(ways[0]).ToList()),
                    NextWayChange = ChangeWayTime * Range(0.6f, 1.4f),
                };
                bot.Trail.Add(start);
                Bots.Add(bot);
                Next(bot);
            }
        }

        private float Range(float a, float b) => a + (float)_random.NextDouble() * (b - a);

        private List<PatrolWay> Ways(bool includeBoss) =>
            _zone.GetComponentsInChildren<PatrolWay>(true).Where(w => w.gameObject.activeInHierarchy && TarkovPatrolGenerator.MainPoints(w).Count > 0 &&
                                                                       (includeBoss || w.PatrolType != PatrolType.boss)).ToList();

        private PatrolWay PickWay(List<PatrolWay> ways) => ways[_random.Next(ways.Count)];

        // FindNextPoint, then the path there (and where a cut path stops)
        private void Next(Bot bot)
        {
            if (bot.Target != null && _owners.TryGetValue(bot.Target, out var owner) && owner == bot)
                _owners.Remove(bot.Target);
            var points = TarkovPatrolGenerator.MainPoints(bot.Way);
            var free = points.Where(p => !_owners.ContainsKey(p) && (p.transform.position - bot.Position).sqrMagnitude > 4f).ToList();
            // nothing free: the game takes any point of the way
            var target = free.Count > 0 ? free[_random.Next(free.Count)] : points[_random.Next(points.Count)];
            bot.Target = target;
            _owners[target] = bot;
            if (!NavMesh.CalculatePath(bot.Position, target.transform.position, NavMesh.AllAreas, _path) || _path.corners.Length < 2)
            {
                // PatrollingData ignores the path status: the bot stands, "walking"
                bot.Path = null;
                bot.State = $"stuck: no path to {target.name}";
                bot.Wait = 3f;
                return;
            }
            bot.Path = _path.corners;
            bot.Corner = 1;
            bot.Walked = 0f;
            float length = TarkovPatrolGenerator.PathLength(_path);
            bool cut = bot.Way.PatrolType == PatrolType.patrolling && _random.Next(100) < 75;
            bot.StopAfter = cut ? length * Range(0.4f, 0.65f) : float.MaxValue;
            bot.State = (_path.status == NavMeshPathStatus.PathComplete ? "walking to " : "walking (path incomplete) to ") + target.name + (cut ? " (part way)" : "");
        }

        public void Step(float dt)
        {
            Time += dt;
            foreach (var bot in Bots)
            {
                if (bot.Way == null)
                    continue;
                if (Time >= bot.NextWayChange)
                {
                    // ShallChangeWay: TryToFindWay, then the next try later either way
                    if (_random.Next(100) >= 50)
                    {
                        var others = Ways(false).Where(w => w != bot.Way).ToList();
                        if (others.Count > 0)
                        {
                            bot.Way = PickWay(others);
                            WayChanges++;
                            bot.Wait = 0f;
                            Next(bot);
                        }
                    }
                    bot.NextWayChange = Time + ChangeWayTime * Range(0.6f, 1.4f);
                }
                if (bot.Wait > 0f)
                {
                    bot.Wait -= dt;
                    if (bot.Wait <= 0f)
                        Next(bot);
                    continue;
                }
                if (bot.Path == null)
                {
                    Next(bot);
                    continue;
                }
                float move = WalkSpeed * dt;
                while (move > 0f && bot.Corner < bot.Path.Length)
                {
                    var to = bot.Path[bot.Corner];
                    float d = Vector3.Distance(bot.Position, to);
                    float step = Mathf.Min(move, d, bot.StopAfter - bot.Walked);
                    bot.Position = Vector3.MoveTowards(bot.Position, to, step);
                    bot.Walked += step;
                    move -= step;
                    if (bot.Walked >= bot.StopAfter)
                        break;
                    if (step >= d - 1e-4f)
                        bot.Corner++;
                    if (step <= 0f)
                        break;
                }
                if (bot.Trail.Count == 0 || (bot.Trail[bot.Trail.Count - 1] - bot.Position).sqrMagnitude > 1f)
                {
                    bot.Trail.Add(bot.Position);
                    if (bot.Trail.Count > 400)
                        bot.Trail.RemoveAt(0);
                }
                if (bot.Walked >= bot.StopAfter)
                {
                    // a cut path: it picks again from where it is
                    bot.State = "changing its mind";
                    bot.Wait = 0.5f;
                }
                else if (bot.Corner >= bot.Path.Length)
                {
                    var target = bot.Target;
                    Visits[target] = Visits.TryGetValue(target, out var n) ? n + 1 : 1;
                    if (bot.Way.PatrolType == PatrolType.reserved)
                    {
                        bot.Wait = ReserveStayTime;
                        bot.State = $"guarding {target.name}";
                    }
                    else if (target.PatrolPointType == PatrolPointType.stayPoint)
                    {
                        bot.Wait = StayTime * Range(0.7f, 1.3f);
                        bot.State = (target.ShallSit ? "sitting at " : "staying at ") + target.name;
                    }
                    else
                    {
                        bot.Wait = CheckTime * Range(0.7f, 1.3f);
                        bot.State = "looking round at " + target.name;
                    }
                }
            }
        }
    }
}
