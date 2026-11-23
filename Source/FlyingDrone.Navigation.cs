using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace FPVDrones;

public partial class FlyingDrone
{
    private const float DiagonalDistance = 1.41421356f;
    private static readonly IntVec3[] StepDirections =
    {
        IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West,
        IntVec3.NorthEast, IntVec3.SouthEast, IntVec3.SouthWest, IntVec3.NorthWest
    };

    private static float Distance(IntVec3 from, IntVec3 to)
    {
        return MathF.Sqrt((from - to).LengthHorizontalSquared);
    }

    private bool IsInPilotingRange(IntVec3 cell)
    {
        return HomeAnchor.IsValid &&
            (cell - HomeAnchor).LengthHorizontalSquared <= MaxRange * MaxRange;
    }

    private bool IsFreeFlightCell(IntVec3 cell)
    {
        if (!cell.InBounds(Map))
        {
            return false;
        }

        Building building = cell.GetEdifice(Map);
        if (building is Building_Door door)
        {
            return door.Open;
        }

        return (building == null || building.def.passability != Traversability.Impassable) && cell.Walkable(Map);
    }

    private bool IsWallTransitCell(IntVec3 cell)
    {
        return cell.InBounds(Map) && cell.GetEdifice(Map)?.def.IsWall == true;
    }

    private bool IsLandingCell(IntVec3 cell)
    {
        if (!IsInPilotingRange(cell) || !IsFreeFlightCell(cell) || !cell.Standable(Map))
        {
            return false;
        }

        Pawn occupant = cell.GetFirstPawn(Map);
        return occupant == null || occupant == this && cell == Position;
    }

    private bool CanTravelStep(IntVec3 from, IntVec3 to, bool crossedWall)
    {
        if (!IsInPilotingRange(to) || !IsFreeFlightCell(to))
        {
            return false;
        }

        IntVec3 offset = to - from;
        if (crossedWall)
        {
            IntVec3 transit = DroneAirspace.TransitCell(from, to);
            return IsWallTransitCell(transit) &&
                !Map.roofGrid.Roofed(from) && !Map.roofGrid.Roofed(transit) && !Map.roofGrid.Roofed(to);
        }

        if (offset.x != 0 && offset.z != 0)
        {
            return IsFreeFlightCell(new IntVec3(from.x + offset.x, 0, from.z)) &&
                IsFreeFlightCell(new IntVec3(from.x, 0, from.z + offset.z));
        }

        return true;
    }

    private bool TryFindPath(IntVec3 start, IntVec3 goal, out List<IntVec3> result, out float distance, bool avoidDrones = false)
    {
        if (!IsFreeFlightCell(goal) || !IsInPilotingRange(goal))
        {
            result = null;
            distance = 0f;
            return false;
        }

        return TryFindPath(start, new[] { goal }, out _, out result, out distance, avoidDrones);
    }

    private bool TryFindPath(IntVec3 start, IReadOnlyList<IntVec3> goals, out IntVec3 destination,
        out List<IntVec3> result, out float distance, bool avoidDrones = false)
    {
        destination = IntVec3.Invalid;
        result = null;
        distance = 0f;
        HashSet<IntVec3> occupied = avoidDrones ? DroneAirspace.OccupiedCells(this) : null;
        if (occupied is { Count: > 0 })
            goals = goals.Where(cell => !occupied.Contains(cell)).ToArray();
        if (goals.Count == 0)
        {
            return false;
        }

        PriorityQueue<(IntVec3 Cell, float Travelled), (float EstimatedTotal, float EstimatedRemaining)> open = new();
        Dictionary<IntVec3, (IntVec3 Previous, float Travelled)> links = new();
        float initialDistance = EstimateRemainingDistance(start, goals);
        open.Enqueue((start, 0f), (initialDistance, initialDistance));
        links.Add(start, (IntVec3.Invalid, 0f));

        while (open.TryDequeue(out (IntVec3 Cell, float Travelled) entry, out _))
        {
            if (links[entry.Cell].Travelled < entry.Travelled)
            {
                continue;
            }

            if (EstimateRemainingDistance(entry.Cell, goals) == 0f)
            {
                destination = entry.Cell;
                result = new List<IntVec3>();
                for (IntVec3 cell = destination; cell != start; cell = links[cell].Previous)
                {
                    result.Add(cell);
                }

                result.Reverse();
                distance = entry.Travelled;
                return true;
            }

            foreach (IntVec3 direction in StepDirections)
            {
                IntVec3 neighbor = entry.Cell + direction;
                bool diagonal = direction.x != 0 && direction.z != 0;
                bool wall = !diagonal && IsWallTransitCell(neighbor);
                if (wall)
                {
                    neighbor += direction;
                }

                if (!CanTravelStep(entry.Cell, neighbor, wall) || occupied != null &&
                    (occupied.Contains(neighbor) || wall && occupied.Contains(DroneAirspace.TransitCell(entry.Cell, neighbor))))
                {
                    continue;
                }

                float travel = entry.Travelled + (diagonal ? DiagonalDistance : wall ? 2f : 1f);
                if (links.TryGetValue(neighbor, out var previous) && previous.Travelled <= travel)
                {
                    continue;
                }

                links[neighbor] = (entry.Cell, travel);
                float remaining = EstimateRemainingDistance(neighbor, goals);
                open.Enqueue((neighbor, travel), (travel + remaining, remaining));
            }
        }

        return false;
    }

    private static float EstimateRemainingDistance(IntVec3 from, IReadOnlyList<IntVec3> destinations)
    {
        float shortest = float.PositiveInfinity;
        foreach (IntVec3 cell in destinations)
        {
            int dx = Math.Abs(from.x - cell.x);
            int dz = Math.Abs(from.z - cell.z);
            int diagonal = Math.Min(dx, dz);
            float distance = Math.Max(dx, dz) - diagonal + DiagonalDistance * diagonal;
            shortest = MathF.Min(shortest, distance);
        }

        return shortest;
    }

    private bool TryFindLandingPath(IntVec3 start, IntVec3 around, bool preferAround, out IntVec3 cell,
        out List<IntVec3> path, out float distance, bool avoidDrones = false)
    {
        if (preferAround && IsLandingCell(around) && TryFindPath(start, around, out List<IntVec3> directPath,
                out float directDistance, avoidDrones))
        {
            cell = around;
            path = directPath;
            distance = directDistance;
            return true;
        }

        var alternatives = new List<IntVec3>(StepDirections.Length);
        foreach (IntVec3 direction in StepDirections)
        {
            IntVec3 candidate = around + direction;
            if (IsLandingCell(candidate))
            {
                alternatives.Add(candidate);
            }
        }

        return TryFindPath(start, alternatives, out cell, out path, out distance, avoidDrones);
    }

    private IntVec3 FindNearestLandingCell(IntVec3 origin)
    {
        if (IsLandingCell(origin))
        {
            return origin;
        }

        foreach (IntVec3 candidate in GenRadial.RadialCellsAround(origin, 3, false))
        {
            if (IsLandingCell(candidate))
            {
                return candidate;
            }
        }

        return origin;
    }
}
