using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace FPVDrones;

public static class DroneFlightOrders
{
    internal static bool IsPlayerFlight(FlyingDrone drone) =>
        drone is { Spawned: true, FlightActive: true } && drone.Faction == Faction.OfPlayer;

    internal static Thing TargetAt(IntVec3 cell, Map map)
    {
        if (map == null || !cell.InBounds(map) || cell.Fogged(map))
            return null;

        Thing building = null;
        foreach (Thing thing in cell.GetThingList(map))
        {
            if (!IsAttackTarget(thing, map))
                continue;
            if (thing is Pawn)
                return thing;
            building ??= thing;
        }

        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            if (IsMultiCellPawn(pawn) && pawn.OccupiedRect().Contains(cell) && IsAttackTarget(pawn, map))
                return pawn;

        return building;
    }

    internal static bool IsMultiCellPawn(Thing thing) =>
        thing is Pawn && (thing.def.size.x > 1 || thing.def.size.z > 1);

    internal static bool IsAttackTarget(Thing target, Map map) =>
        target is Pawn or Building && target.Spawned && target.Map == map &&
        target.HostileTo(Faction.OfPlayer) && !target.Position.Fogged(map) &&
        (target is not Pawn pawn || !pawn.Dead);

    public static bool Issue(Selector selector, IntVec3 cell, Thing target, bool append)
    {
        var drones = selector.SelectedObjects.OfType<FlyingDrone>().Where(IsPlayerFlight).ToList();
        if (drones.Count == 0)
            return false;

        bool sent = false;
        if (!append && IsAttackTarget(target, drones[0].Map))
        {
            foreach (var drone in drones)
                if (drone.TryAssignAttack(target)) sent = true;
        }
        else if (cell.InBounds(drones[0].Map))
        {
            var candidates = GenRadial.RadialCellsAround(cell, 2.9f, true);
            foreach (var drone in drones)
            {
                var destination = candidates.FirstOrDefault(drone.AcceptsWaypoint, IntVec3.Invalid);
                if (!destination.IsValid) continue;
                drone.SetWaypoint(destination, append);
                sent = true;
            }
        }

        if (!sent)
            Messages.Message("FPV_OrderUnavailable".Translate(), MessageTypeDefOf.RejectInput, false);
        return true;
    }
}

public partial class FlyingDrone
{
    private Thing assignedAttackTarget;
    private IntVec3 attackApproach = IntVec3.Invalid;
    private IntVec3 attackImpact = IntVec3.Invalid;
    private IntVec3 observedTargetCell = IntVec3.Invalid;
    private int nextAttackRouteCheck;

    public Thing AttackTarget => assignedAttackTarget;

    internal bool AcceptsWaypoint(IntVec3 cell) =>
        flying && !Returning && !FinalApproachActive && batteryTicks > 0 && IsFreeFlightCell(cell) && IsInPilotingRange(cell) &&
        DroneAirspace.IsWaypointFree(this, cell);

    internal bool TryAssignAttack(Thing target)
    {
        if (FinalApproachActive)
            return target == assignedAttackTarget;

        if (!DroneFlightOrders.IsPlayerFlight(this) || IsScout || Returning || batteryTicks <= 0 ||
            !DroneFlightOrders.IsAttackTarget(target, Map) || !PlanAttack(target, out var approach, out var impact))
            return false;

        if (strikingWall) ClearSegment();
        assignedAttackTarget = target;
        ApplyAttackRoute(target, approach, impact);
        return true;
    }

    private bool PlanAttack(Thing target, out IntVec3 approach, out IntVec3 impact)
    {
        approach = impact = target.Position;
        if (!DroneFlightOrders.IsMultiCellPawn(target))
        {
            if (!IsInPilotingRange(impact)) return false;
            if (CanReachRaidCell(approach)) return true;
            if (target is not Building) return false;
        }

        CellRect occupied = target.OccupiedRect();
        var checkedCells = new HashSet<IntVec3>();
        foreach (var cell in occupied.EdgeCells.OrderBy(cell => (Position - cell).LengthHorizontalSquared))
        {
            if (!IsInPilotingRange(cell)) continue;
            foreach (var direction in GenAdj.CardinalDirections)
            {
                IntVec3 nearby = cell + direction;
                if (occupied.Contains(nearby) || !checkedCells.Add(nearby) || !CanReachRaidCell(nearby))
                    continue;
                approach = nearby;
                impact = cell;
                return true;
            }
        }
        return false;
    }

    private void ApplyAttackRoute(Thing target, IntVec3 approach, IntVec3 impact)
    {
        attackApproach = approach;
        attackImpact = impact;
        observedTargetCell = target.Position;
        nextAttackRouteCheck = Find.TickManager.TicksGame + 10;
        AssignWaypoint(approach, false);
    }

    private void ClearAttackOrder()
    {
        assignedAttackTarget = null;
        attackApproach = attackImpact = observedTargetCell = IntVec3.Invalid;
        ClearFinalApproach();
    }

    private bool HandleAttackPawnContact(IntVec3 cell, int now, bool reachedEnd)
    {
        if (FinalApproachActive)
        {
            if (cell != finalApproachCell)
                return false;

            // Do not enter the pawn grid at mid-segment before the impact resolves.
            if (reachedEnd)
                FinishFinalApproach(now);
            return true;
        }

        if (assignedAttackTarget is not Pawn target ||
            !target.OccupiedRect().Contains(cell) || !DroneFlightOrders.IsAttackTarget(target, Map))
            return false;

        if (DroneAirspace.HasAttackTurn(this))
            TryCommitFinalApproach(target, now);
        return true;
    }

    private void UpdateAttackOrder(int now)
    {
        if (FinalApproachActive)
        {
            UpdateFinalApproach(now);
            return;
        }

        if (assignedAttackTarget == null)
        {
            if (attackApproach.IsValid) StopAttackRoute();
            return;
        }
        if (strikingWall) return;
        if (!DroneFlightOrders.IsAttackTarget(assignedAttackTarget, Map))
        {
            StopAttackRoute();
            return;
        }
        if (!DroneAirspace.HasAttackTurn(this)) return;
        if (assignedAttackTarget is Pawn target && TryCommitFinalApproach(target, now)) return;

        CellRect occupied = assignedAttackTarget.OccupiedRect();
        bool contactStillValid = assignedAttackTarget.Position == observedTargetCell && occupied.Contains(attackImpact) &&
            (attackApproach == attackImpact || !occupied.Contains(attackApproach));
        if (assignedAttackTarget is Building && contactStillValid && !segmentTo.IsValid && Position == attackApproach)
        {
            if (attackApproach != attackImpact && IsWallTransitCell(attackImpact))
                EndRaidAtWall(attackImpact);
            else
                CompleteFlight(false, false, "FPV_EndAttack", attackImpact, assignedAttackTarget);
            return;
        }

        if (now < nextAttackRouteCheck || contactStillValid && !routeBlocked)
            return;

        if (PlanAttack(assignedAttackTarget, out var approach, out var impact))
            ApplyAttackRoute(assignedAttackTarget, approach, impact);
        else
            StopAttackRoute();
    }

    private void StopAttackRoute()
    {
        ClearAttackOrder();
        waypoints.Clear();
        ClearPath();
        Messages.Message("FPV_AttackOrderLost".Translate(), this, MessageTypeDefOf.NeutralEvent, false);
    }

    private void ExposeAttackOrder()
    {
        Scribe_References.Look(ref assignedAttackTarget, "fpvAssignedAttackTarget");
        Scribe_Values.Look(ref attackApproach, "fpvAttackApproach", IntVec3.Invalid);
        Scribe_Values.Look(ref attackImpact, "fpvAttackImpact", IntVec3.Invalid);
        Scribe_Values.Look(ref observedTargetCell, "fpvObservedAttackCell", IntVec3.Invalid);
        ExposeFinalApproach();
    }
}
