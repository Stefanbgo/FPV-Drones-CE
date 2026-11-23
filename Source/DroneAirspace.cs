using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace FPVDrones;

internal static class DroneAirspace
{
    private static IEnumerable<FlyingDrone> OtherFlights(Map map, FlyingDrone except)
    {
        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            if (pawn is FlyingDrone { Spawned: true, FlightActive: true } drone && drone != except)
                yield return drone;
    }

    internal static bool IsFree(Map map, IntVec3 cell, FlyingDrone except = null)
    {
        foreach (var drone in OtherFlights(map, except))
            if (drone.ClaimsAirCell(cell)) return false;
        return true;
    }

    internal static bool IsWaypointFree(FlyingDrone drone, IntVec3 cell)
    {
        foreach (var other in OtherFlights(drone.Map, drone))
            if (other.ClaimsAirCell(cell) || other.HasHoverWaypoint(cell)) return false;
        return true;
    }

    internal static bool IsStepFree(FlyingDrone drone, IntVec3 from, IntVec3 to)
    {
        var transit = TransitCell(from, to);
        foreach (var other in OtherFlights(drone.Map, drone))
            if (other.ClaimsAirCell(to) || transit.IsValid && other.ClaimsAirCell(transit)) return false;
        return true;
    }

    internal static HashSet<IntVec3> OccupiedCells(FlyingDrone drone)
    {
        var cells = new HashSet<IntVec3>();
        foreach (var other in OtherFlights(drone.Map, drone))
            other.AddAirCells(cells);
        return cells;
    }

    internal static bool HasAttackTurn(FlyingDrone drone)
    {
        if (drone.AttackTarget is not { } target) return true;
        int distance = (drone.Position - target.Position).LengthHorizontalSquared;
        foreach (var other in OtherFlights(drone.Map, drone))
        {
            if (other.AttackTarget != target) continue;
            int otherDistance = (other.Position - target.Position).LengthHorizontalSquared;
            if (otherDistance < distance || otherDistance == distance && other.thingIDNumber < drone.thingIDNumber)
                return false;
        }
        return true;
    }

    internal static IntVec3 TransitCell(IntVec3 from, IntVec3 to) => (to - from).LengthHorizontalSquared == 4
        ? new IntVec3((from.x + to.x) / 2, 0, (from.z + to.z) / 2)
        : IntVec3.Invalid;

    internal static IntVec3 FindLaunchCell(Pawn pilot)
    {
        Map map = pilot.Map;
        if (IsFree(map, pilot.Position)) return pilot.Position;
        if (pilot.Faction != Faction.OfPlayer) return IntVec3.Invalid;

        foreach (var cell in GenRadial.RadialCellsAround(pilot.Position, 1.5f, false))
            if (cell.InBounds(map) && cell.Standable(map) && IsFree(map, cell) &&
                pilot.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                return cell;
        return IntVec3.Invalid;
    }
}

public partial class FlyingDrone
{
    private bool restoreAirspace;

    internal bool ClaimsAirCell(IntVec3 cell) => cell == Position || segmentTo.IsValid &&
        (cell == segmentFrom || cell == segmentTo || cell == DroneAirspace.TransitCell(segmentFrom, segmentTo));

    internal void AddAirCells(HashSet<IntVec3> cells)
    {
        cells.Add(Position);
        if (!segmentTo.IsValid) return;
        cells.Add(segmentFrom);
        cells.Add(segmentTo);
        var transit = DroneAirspace.TransitCell(segmentFrom, segmentTo);
        if (transit.IsValid) cells.Add(transit);
    }

    internal bool HasHoverWaypoint(IntVec3 cell) => Returning ? cell == landingTarget :
        AttackTarget == null && waypoints.Contains(cell);

    private bool RestoreAirspace()
    {
        bool overlapping = false;
        foreach (Thing thing in Position.GetThingList(Map))
        {
            if (thing is FlyingDrone { FlightActive: true } other && other.thingIDNumber < thingIDNumber)
            {
                overlapping = true;
                break;
            }
        }

        if (overlapping)
        {
            foreach (var cell in GenRadial.RadialCellsAround(Position, 5f, false))
            {
                if (!IsInPilotingRange(cell) || !IsFreeFlightCell(cell) || !DroneAirspace.IsFree(Map, cell, this))
                    continue;
                ReachFreeCell(cell);
                ClearSegment();
                ClearPath();
                nextRouteCheck = 0;
                restoreAirspace = false;
                return true;
            }
            return false;
        }

        if (segmentTo.IsValid && (!DroneAirspace.IsFree(Map, segmentFrom, this) ||
            !DroneAirspace.IsStepFree(this, segmentFrom, segmentTo)))
        {
            ClearSegment();
            ClearPath();
            nextRouteCheck = 0;
        }
        restoreAirspace = false;
        return true;
    }
}
