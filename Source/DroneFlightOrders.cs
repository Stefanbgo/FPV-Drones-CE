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
        return building;
    }

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
        else
        {
            foreach (var drone in drones)
            {
                if (!drone.AcceptsWaypoint(cell)) continue;
                drone.SetWaypoint(cell, append);
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
        flying && !Returning && batteryTicks > 0 && IsFreeFlightCell(cell) && IsInPilotingRange(cell);

    internal bool TryAssignAttack(Thing target)
    {
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
        if (!IsInPilotingRange(impact)) return false;
        if (CanReachRaidCell(approach)) return true;
        if (target is not Building building) return false;

        CellRect occupied = building.OccupiedRect();
        var checkedCells = new HashSet<IntVec3>();
        foreach (var cell in occupied.Cells)
        {
            foreach (var direction in GenAdj.CardinalDirections)
            {
                IntVec3 nearby = cell + direction;
                if (occupied.Contains(nearby) || !checkedCells.Add(nearby) || !IsInPilotingRange(cell) || !CanReachRaidCell(nearby))
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
    }

    private void UpdateAttackOrder(int now)
    {
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

        if (assignedAttackTarget.Position == observedTargetCell && !segmentTo.IsValid && Position == attackApproach)
        {
            if (attackApproach != attackImpact && IsWallTransitCell(attackImpact))
                EndRaidAtWall(attackImpact);
            else
                CompleteFlight(false, false, "FPV_EndAttack", attackImpact);
            return;
        }

        if (now < nextAttackRouteCheck || assignedAttackTarget.Position == observedTargetCell && !routeBlocked)
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
    }
}
