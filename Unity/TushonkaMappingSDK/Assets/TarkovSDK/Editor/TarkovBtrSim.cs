using System;
using System.Collections.Generic;
using System.Linq;
using BezierSplineTools;
using EFT.Vehicle;
using UnityEngine;

namespace TarkovSDK.Editor
{
    // An edit-mode port of the game's VehicleBase driving (the BTR), for the path editor's preview and Build Map's
    // route check. The decisions are line-faithful: the route's parts (MapPathConfig.GetPathParts, keyed by id, so a
    // point listed twice or an exit equal to the enter point drops out), ToNextDestination / MoveToDestination,
    // MovePauseOff's CheckNeedReverse (GetPoint(0.01) by curve index, front or back turn by the stop's forward), the
    // reverse parts, and MoveByPathSpline's heading: it looks along its path, turned half way each step, but keeps
    // its heading when the path is 90 degrees or more off it (so it slides sideways). The speed follows the game's
    // multiplier (slower in turns, slowing to the end of a road, 0.1 per second up) on an arc-length step instead of
    // its calibration loop. Pauses are the preview's own.
    public sealed class TarkovBtrSim
    {
        public enum EventKind { Info, Turn, Slide, Problem, Finished }

        public sealed class Event
        {
            public EventKind Kind;
            public string Text;
            public Vector3 Position;
            public float Time;
        }

        // VehicleBase's defaults (the BTR prefab keeps them)
        private const float CheckTurnDistanceTime = 10f, TurnCheckSensitivity = 0.98f, DecreaseSpeedOnTurnLimit = 0.5f;
        private const float EndSplineDecelerationDistance = 2f, AccelerationSpeed = 0.1f, DecelerationSpeed = 0.1f;
        private const float HorizontalRotateLerp = 0.5f, PauseBeforeMove = 1f;
        public const float Step = 0.02f;

        private readonly MapPathConfig _config;
        private readonly PathConfig _route;
        private readonly List<KeyValuePair<string, Component>> _elements = new List<KeyValuePair<string, Component>>();
        private readonly Dictionary<PathSpline, Track> _tracks = new Dictionary<PathSpline, Track>();

        public float MoveSpeed = 8f;
        // seconds it waits at a stop in the preview (the raid: TarkovBtrSettings' Pause Duration)
        public float PauseSeconds = 2f;
        // laps driven before it leaves (the route's own count, capped for the check)
        public int Laps;

        public Vector3 Position { get; private set; }
        public Quaternion Rotation { get; private set; }
        public bool Finished { get; private set; }
        public bool Paused => _pauseOn;
        public bool Backward => _reverseOn && _part.moveType == ReverseMoveType.backward;
        public bool Reversing => _reverseOn;
        // degrees between its heading and the way it's going, while it keeps its heading
        public float SlideAngle { get; private set; }
        public float Time { get; private set; }
        public float Distance { get; private set; }
        public string Where => _spline != null ? _spline.id : _current != null ? _current.id : "";
        public readonly List<Event> Events = new List<Event>();
        public readonly List<Vector3> Trail = new List<Vector3>();

        private PathSpline _spline, _afterReverse;
        private PathDestination _current, _target;
        private PathReverseData _reverseData;
        private PathReversePart _part;
        private bool _moveByPath, _goingForward = true, _moveBySplineOn, _reverseOn, _needCheckNext = true, _checkEndSpline = true, _pauseOn;
        private float _progress, _multiplier = 0.1f, _stopDistance, _pauseBeforeMove, _pauseTime, _slideStart;
        private int _circleCount, _reverseIndex;
        private bool _sliding;
        private Vector3 _slideFrom;

        public TarkovBtrSim(MapPathConfig config, PathConfig route)
        {
            _config = config;
            _route = route;
            Laps = route.circleCount;
            // MapPathConfig.GetPathParts over GetAllPathPoints: a road is added only when its *point's* id isn't a key
            // yet, the point only once
            var ids = new List<string>();
            if (!string.IsNullOrEmpty(route.enterPoint))
                ids.Add(route.enterPoint);
            ids.AddRange(route.pathPoints ?? new List<string>());
            if (!string.IsNullOrEmpty(route.exitPoint))
                ids.Add(route.exitPoint);
            var keys = new HashSet<string>();
            string previous = null;
            foreach (var id in ids)
            {
                if (!string.IsNullOrEmpty(previous))
                {
                    var road = Road(previous, id);
                    if (road != null && !keys.Contains(id) && keys.Add(road.id))
                        _elements.Add(new KeyValuePair<string, Component>(road.id, road));
                }
                var part = (Component)Stop(id) ?? _config.PathSplines.FirstOrDefault(s => s != null && s.id == id);
                if (part != null && keys.Add(id))
                    _elements.Add(new KeyValuePair<string, Component>(id, part));
                previous = id;
            }
        }

        private PathDestination Stop(string id) => _config.PathDestinations.FirstOrDefault(s => s != null && s.id == id);

        // MapPathConfig.GetPathSplineByPoints: s_a_b or s_b_a
        private PathSpline Road(string a, string b) =>
            _config.PathSplines.FirstOrDefault(s => s != null && (s.id == $"s_{a}_{b}" || s.id == $"s_{b}_{a}"));

        private int IndexOf(Component part) => _elements.FindIndex(e => e.Value == part);

        // ------------------------------------------------------------ the game's flow

        // MoveEnable: at the enter point, facing its forward, straight onto the first road
        public bool Start()
        {
            if (_route.pathPoints == null || _route.pathPoints.Count == 0)
            {
                Problem("the route has no stops", Vector3.zero);
                return false;
            }
            var enter = Stop(_route.enterPoint);
            if (enter == null)
            {
                Problem($"the route's enter point '{_route.enterPoint}' doesn't exist", Vector3.zero);
                return false;
            }
            Position = enter.transform.position;
            Rotation = Quaternion.LookRotation(Flat(enter.transform.forward, Vector3.forward), Vector3.up);
            _current = _target = enter;
            _moveByPath = true;
            Trail.Add(Position);
            Info($"appears at {enter.id}", Position);
            ToNextDestination();
            if (_spline == null && !Finished)
                Problem($"no road from {enter.id} to the route's first stop", Position);
            return !Finished;
        }

        public void Tick()
        {
            if (Finished)
                return;
            Time += Step;
            if (_pauseOn)
            {
                _pauseTime += Step;
                if (_pauseTime >= PauseSeconds)
                    MovePauseOff();
                return;
            }
            if (_moveBySplineOn && _spline != null)
                MoveByPathSpline();
        }

        // runs it to the end without drawing (Build Map's check); false if it never finished
        public bool RunToEnd(float maxSeconds = 3600f)
        {
            if (!Start())
                return false;
            while (!Finished && Time < maxSeconds)
                Tick();
            if (!Finished)
                Problem("still driving after an hour of raid time (does it reach its exit?)", Position);
            return true;
        }

        private void ToNextDestination()
        {
            if (!_moveByPath)
                return;
            int next = IndexOf(_target) + 1;
            if (_route.once)
            {
                if (next >= _elements.Count - 1)
                {
                    _moveByPath = false;
                    return;
                }
                SetLeg(next);
            }
            else if (_route.circle)
            {
                if (next >= _elements.Count - 1)
                {
                    _circleCount++;
                    MoveToDestination(_route.pathPoints[0]);
                    return;
                }
                if (_circleCount >= Laps)
                {
                    _moveByPath = false;
                    MoveToDestination(_route.exitPoint);
                    return;
                }
                SetLeg(next);
            }
        }

        private void SetLeg(int index)
        {
            var road = index < _elements.Count ? _elements[index].Value as PathSpline : null;
            var stop = index + 1 < _elements.Count ? _elements[index + 1].Value as PathDestination : null;
            if (stop != null)
                _target = stop;
            if (road != null)
            {
                SetSpline(road);
                _moveBySplineOn = true;
                _pauseBeforeMove = 0f;
            }
        }

        private void MoveToDestination(string id)
        {
            var road = _current != null ? Road(_current.id, id) : null;
            if (road == null)
            {
                Problem($"no road from {_current?.id} to {id}: the game throws here and the BTR stops for good", Position);
                return;
            }
            SetSpline(road);
            var stop = Stop(id);
            if (stop != null)
                _target = stop;
            _moveBySplineOn = true;
            _needCheckNext = false;
            _pauseBeforeMove = 0f;
        }

        private void MovePauseOff()
        {
            _pauseOn = false;
            _reverseOn = false;
            _checkEndSpline = true;
            if (_needCheckNext)
                ToNextDestination();
            if (Finished)
                return;
            if (_spline == null)
            {
                // a once route's last stop: nothing next. The game calls CheckNeedReverse with no road anyway
                Finish($"stays at {_current?.id} for good: the route has nothing after it" +
                       (_route.once && _route.exitPoint == _route.enterPoint ? " (a once-through route whose exit is its enter point never drives back to it)" : ""),
                    EventKind.Problem);
                return;
            }
            CheckNeedReverse();
        }

        private void CheckNeedReverse()
        {
            if (_current == null || !_current.useReverse)
            {
                // without turns it just drives off; the heading rule decides whether it turns or slides
                return;
            }
            var forward = Rotation * Vector3.forward;
            var point = TarkovSplines.GetPoint(_spline.spline, _goingForward ? 0.01f : 0.99f);
            point.y = Position.y;
            if (Vector3.Dot(forward, (point - Position).normalized) >= 0f)
            {
                _reverseOn = false;
                return;
            }
            bool back = Vector3.Dot(forward, _current.transform.forward) < 0f;
            _reverseData = back ? _current.backReverseData : _current.frontReverseData;
            if (_reverseData.reverseParts == null || _reverseData.reverseParts.Count == 0 || _reverseData.reverseParts.Any(p => p.spline == null || p.spline.spline == null))
            {
                Problem($"{_current.id}: the next road starts behind it and its {(back ? "back" : "front")} turn is missing (the game throws)", Position);
                return;
            }
            Add(EventKind.Turn, $"three-point turn at {_current.id}: the road to {_target?.id} starts behind it", Position);
            _afterReverse = _spline;
            _reverseOn = true;
            _reverseIndex = 0;
            _part = _reverseData.reverseParts[0];
            SetSpline(_part.spline);
            if (_reverseData.reverseParts.Count == 1)
                _checkEndSpline = false;
            _moveBySplineOn = true;
        }

        private void MoveByPathSpline()
        {
            if (_pauseBeforeMove < PauseBeforeMove)
            {
                _pauseBeforeMove += Step;
                return;
            }
            var track = TrackOf(_spline);
            if (track.Length < 0.01f)
            {
                EndSpline();
                return;
            }
            float sign = _goingForward ? 1f : -1f;
            float stepSize = MoveSpeed * Step * _multiplier;

            // CheckPathTurn / CheckTheEndSpline
            var ahead = track.At(_progress + sign * CheckTurnDistanceTime * stepSize / track.Length);
            var heading = Rotation * Vector3.forward * (Backward ? -1f : 1f);
            var flatPos = Position;
            flatPos.y = ahead.y;
            bool turning = Vector3.Dot(heading, (ahead - flatPos).normalized) < TurnCheckSensitivity;
            var end = track.At(_goingForward ? 1f : 0f);
            end.y = Position.y;
            float toEnd = Vector3.Distance(Position, end);
            bool ending = toEnd < EndSplineDecelerationDistance && _checkEndSpline;
            if (turning && !ending)
            {
                if (_multiplier >= DecreaseSpeedOnTurnLimit)
                {
                    _multiplier = Mathf.Clamp(_multiplier - DecelerationSpeed * Step, DecreaseSpeedOnTurnLimit, 1f);
                    _stopDistance = 0f;
                }
            }
            else if (ending)
            {
                if (_stopDistance == 0f)
                    _stopDistance = toEnd;
                _multiplier = Mathf.Clamp(toEnd / Mathf.Max(_stopDistance, 0.001f), 0.05f, _multiplier);
            }
            else
            {
                _multiplier = Mathf.Clamp(_multiplier + AccelerationSpeed * Step, 0.05f, 1f);
                _stopDistance = 0f;
            }

            float next = _progress + sign * MoveSpeed * Step * _multiplier / track.Length;
            if (_goingForward ? next > 1f : next < 0f)
            {
                EndSpline();
                return;
            }
            var previous = Position;
            var point = track.At(next);
            Position = point;
            Distance += Vector3.Distance(previous, point);
            // lookForward: LookAt the next point (behind it when backing up), half way per step, unless 90 degrees or more off
            var travel = point - previous;
            if (travel.sqrMagnitude > 1e-8f)
            {
                var look = Quaternion.LookRotation(Backward ? -travel : travel, Vector3.up);
                float angle = Quaternion.Angle(Rotation, look);
                if (angle < 90f && angle != 0f)
                {
                    Rotation = Quaternion.Lerp(Rotation, look, HorizontalRotateLerp);
                    EndSlide();
                }
                else if (angle >= 90f)
                {
                    SlideAngle = angle;
                    if (!_sliding)
                    {
                        _sliding = true;
                        _slideFrom = Position;
                        _slideStart = Distance;
                    }
                }
            }
            if (Trail.Count == 0 || Vector3.Distance(Trail[Trail.Count - 1], Position) > 1f)
                Trail.Add(Position);
            _progress = next;
        }

        private void EndSlide()
        {
            SlideAngle = 0f;
            if (!_sliding)
                return;
            _sliding = false;
            float metres = Distance - _slideStart;
            if (metres > 0.5f)
                Add(EventKind.Slide, $"slides sideways for {metres:0} m on {_spline?.id}: the road turns 90 degrees or more under it", _slideFrom);
        }

        private void EndSpline()
        {
            EndSlide();
            _progress = 0f;
            _goingForward = true;
            _moveBySplineOn = false;
            if (_checkEndSpline)
                _multiplier = 0.1f;
            if (!_reverseOn)
                IncomingToDestination(_target);
            else
                NextReversePart();
        }

        private void NextReversePart()
        {
            _reverseIndex++;
            if (_reverseIndex < _reverseData.reverseParts.Count)
            {
                if (_reverseIndex == _reverseData.reverseParts.Count - 1)
                    _checkEndSpline = false;
                _part = _reverseData.reverseParts[_reverseIndex];
                SetSpline(_part.spline);
                _moveBySplineOn = true;
                return;
            }
            // EndReverse, then on at once
            SetSpline(_afterReverse);
            _moveBySplineOn = true;
            _reverseOn = false;
            _reverseIndex = 0;
            _checkEndSpline = true;
            MoveByPathSpline();
        }

        private void IncomingToDestination(PathDestination stop)
        {
            if (stop == null)
            {
                Problem("drove to a stop that doesn't exist", Position);
                return;
            }
            Position = stop.transform.position;
            _multiplier = 0.1f;
            _needCheckNext = true;
            _spline = null;
            _current = stop;
            Trail.Add(Position);
            if (stop.id == _route.exitPoint)
            {
                Finish($"leaves at {stop.id} after {Time / 60f:0.0} min, {Distance:0} m", EventKind.Finished);
                return;
            }
            Info($"stops at {stop.id}", Position);
            _pauseOn = true;
            _pauseTime = 0f;
        }

        // SetPathSplineAsCurrent: it drives the road from whichever end is nearer
        private void SetSpline(PathSpline road)
        {
            _spline = road;
            if (road == null || road.spline == null || road.spline.points == null || road.spline.points.Length < 4)
            {
                Problem($"road '{road?.id}' has no curve", Position);
                return;
            }
            var pts = road.spline.points;
            var t = road.spline.transform;
            bool reverse = Vector3.Distance(Position, t.TransformPoint(pts[pts.Length - 1])) < Vector3.Distance(Position, t.TransformPoint(pts[0]));
            _progress = reverse ? 1f : 0f;
            _goingForward = !reverse;
        }

        private Track TrackOf(PathSpline road)
        {
            if (!_tracks.TryGetValue(road, out var track))
                _tracks[road] = track = new Track(road.spline);
            return track;
        }

        private void Info(string text, Vector3 at) => Add(EventKind.Info, text, at);

        private void Problem(string text, Vector3 at) => Finish(text, EventKind.Problem, at);

        private void Finish(string text, EventKind kind, Vector3? at = null)
        {
            Add(kind, text, at ?? Position);
            Finished = true;
        }

        private void Add(EventKind kind, string text, Vector3 at) => Events.Add(new Event { Kind = kind, Text = text, Position = at, Time = Time });

        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v = Vector3.ProjectOnPlane(v, Vector3.up);
            return v.sqrMagnitude > 1e-6f ? v.normalized : fallback;
        }

        // the spline by arc length (BezierSpline.GetPointWithCurvesLengthCache)
        private sealed class Track
        {
            private readonly List<Vector3> _points = new List<Vector3>();
            private readonly List<float> _lengths = new List<float>();
            public readonly float Length;

            public Track(BezierSpline spline)
            {
                _points = TarkovSplines.Sample(spline, 24);
                float total = 0f;
                _lengths.Add(0f);
                for (int i = 1; i < _points.Count; i++)
                {
                    total += Vector3.Distance(_points[i - 1], _points[i]);
                    _lengths.Add(total);
                }
                Length = total;
            }

            public Vector3 At(float t)
            {
                if (_points.Count == 0)
                    return Vector3.zero;
                float d = Mathf.Clamp01(t) * Length;
                int i = _lengths.BinarySearch(d);
                if (i >= 0)
                    return _points[i];
                i = ~i;
                if (i <= 0)
                    return _points[0];
                if (i >= _points.Count)
                    return _points[_points.Count - 1];
                float span = _lengths[i] - _lengths[i - 1];
                return Vector3.Lerp(_points[i - 1], _points[i], span > 0f ? (d - _lengths[i - 1]) / span : 0f);
            }
        }

        // Build Map's check: drives each active route (at most two laps) and lists its turns, slides and dead ends
        public static List<Event> Check(MapPathConfig config, PathConfig route, float moveSpeed)
        {
            var sim = new TarkovBtrSim(config, route) { MoveSpeed = Mathf.Max(1f, moveSpeed), PauseSeconds = 0f };
            sim.Laps = Mathf.Min(route.circleCount, 2);
            sim.RunToEnd();
            return sim.Events;
        }
    }
}
