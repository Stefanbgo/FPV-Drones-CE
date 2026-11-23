using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FPVDrones;

public partial class FlyingDrone : Pawn
{
    private const int TicksPerSecond = 60;
    private const int RouteRetryTicks = 15;
    private const int ReturnWarningTicks = 5 * TicksPerSecond;
    private const float MovementEpsilon = 0.0001f;

    public Pawn Operator;
    public bool Returning;
    public IntVec3 LastFree = IntVec3.Invalid;
    public IntVec3 HomeAnchor = IntVec3.Invalid;

    private List<IntVec3> waypoints = new();
    private List<IntVec3> path = new();
    private int pathIndex;
    private IntVec3 returnPoint = IntVec3.Invalid;
    private IntVec3 landingTarget = IntVec3.Invalid;
    private IntVec3 segmentFrom = IntVec3.Invalid;
    private IntVec3 segmentTo = IntVec3.Invalid;
    private float segmentTravel;
    private int batteryTicks;
    private int depletedTick = -1;
    private int nextRouteCheck;
    private int nextWarningCheck;
    private bool returnWarningSent;
    private bool reacquirePending;
    private bool flying;
    private bool routeBlocked;
    private bool strikingWall;

    public bool IsScout => def.defName == "FPV_ScoutAir";
    public int MaxRange => IsScout ? 120 : 100;
    public int ChargeTicks => batteryTicks;
    public float Charge01 => batteryTicks / (float)MaximumBatteryTicks;
    public bool CrossingWall => segmentTo.IsValid && (segmentTo - segmentFrom).LengthHorizontalSquared == 4;
    public bool FlightActive => flying;

    private int MaximumBatteryTicks => (IsScout ? 80 : 30) * TicksPerSecond;
    private float CellDistancePerTick => (IsScout ? 4f : 6f) / TicksPerSecond;

    public override Vector3 DrawPos
    {
        get
        {
            if (!segmentTo.IsValid)
            {
                return Position.ToVector3ShiftedWithAltitude(def.Altitude);
            }

            float fraction = segmentTravel / Distance(segmentFrom, segmentTo);
            Vector3 location = Vector3.Lerp(segmentFrom.ToVector3Shifted(), segmentTo.ToVector3Shifted(), fraction);
            location.y = Position.ToVector3ShiftedWithAltitude(def.Altitude).y;
            return location;
        }
    }

    public void BeginFlight(Pawn pawn, float charge)
    {
        Operator = pawn;
        HomeAnchor = pawn.Position;
        LastFree = Spawned ? Position : HomeAnchor;
        batteryTicks = Math.Clamp(Mathf.RoundToInt(charge * MaximumBatteryTicks), 1, MaximumBatteryTicks);
        flying = true;
    }

    public void SetWaypoint(IntVec3 cell, bool append)
    {
        if (!flying || Returning || batteryTicks <= 0)
        {
            return;
        }

        if (!IsFreeFlightCell(cell) || !IsInPilotingRange(cell))
        {
            Messages.Message("FPV_Unreachable".Translate(), MessageTypeDefOf.RejectInput, false);
            return;
        }

        if (AttackTarget != null)
        {
            ClearAttackOrder();
            append = false;
            if (strikingWall) ClearSegment();
        }
        AssignWaypoint(cell, append);
    }

    private void AssignWaypoint(IntVec3 cell, bool append)
    {
        if (!append)
        {
            waypoints.Clear();
            ClearPath();
            nextRouteCheck = 0;
        }

        waypoints.Add(cell);
        routeBlocked = false;
        InvalidateRoutePreview();
    }

    internal bool CanReachRaidCell(IntVec3 cell)
    {
        IntVec3 start = segmentTo.IsValid && !strikingWall ? segmentTo : Position;
        return flying && !Returning && TryFindPath(start, cell, out _, out float distance) &&
            TicksNeeded(distance) <= batteryTicks;
    }

    internal bool TrySetRaidWaypoint(IntVec3 cell)
    {
        if (strikingWall)
            return false;
        if (waypoints.Count == 1 && waypoints[0] == cell)
            return !routeBlocked;
        if (!CanReachRaidCell(cell))
            return false;
        SetWaypoint(cell, false);
        return true;
    }

    internal void EndRaidAtWall(IntVec3 wallCell)
    {
        if (IsScout || !Spawned || segmentTo.IsValid || !wallCell.InBounds(Map) ||
            (Position - wallCell).LengthHorizontalSquared != 1 || !IsWallTransitCell(wallCell))
            return;

        waypoints.Clear();
        ClearPath();
        SetSegment(wallCell);
        strikingWall = true;
    }

    public void ReturnHome()
    {
        if (!flying || !IsScout || Returning || batteryTicks <= 0)
        {
            return;
        }

        IntVec3 start = segmentTo.IsValid ? segmentTo : Position;
        if (!TryFindLandingPath(start, HomeAnchor, false, out IntVec3 point, out var route, out float distance) ||
            TicksNeeded(distance) > batteryTicks)
        {
            Messages.Message("FPV_ReturnNoCharge".Translate(), this, MessageTypeDefOf.RejectInput, false);
            return;
        }

        Returning = true;
        returnPoint = point;
        landingTarget = point;
        waypoints.Clear();
        UsePath(route);
        ReleasePilot();
    }

    public void Reacquire()
    {
        if (!flying || !Returning)
        {
            return;
        }

        if (CrossingWall)
        {
            reacquirePending = true;
            return;
        }

        TryReacquire();
    }

    private void TryReacquire()
    {
        reacquirePending = false;
        if (!OperatorAvailable())
        {
            RejectReacquire("FPV_ReacquireNoPilot");
            return;
        }

        if (!DroneControl.HasKit(Operator))
        {
            RejectReacquire("FPV_ReacquireNoKit");
            return;
        }

        if ((Operator.Position - Position).LengthHorizontalSquared > MaxRange * MaxRange)
        {
            RejectReacquire("FPV_ReacquireOutOfRange");
            return;
        }

        if (batteryTicks <= 0)
        {
            RejectReacquire("FPV_ReacquireBusy");
            return;
        }

        // The pilot job can enter its hold toil before StartPiloting returns.
        Returning = false;
        if (!DroneControl.StartPiloting(Operator, this) || !DroneControl.IsPiloting(Operator, this))
        {
            Returning = true;
            RejectReacquire("FPV_ReacquireBusy");
            return;
        }

        HomeAnchor = Operator.Position;
        returnPoint = IntVec3.Invalid;
        landingTarget = IntVec3.Invalid;
        ClearSegment();
        ClearPath();
        returnWarningSent = false;
        nextWarningCheck = 0;
    }

    private void RejectReacquire(string key)
    {
        Messages.Message(key.Translate(), this, MessageTypeDefOf.RejectInput, false);
    }

    private bool OperatorAvailable()
    {
        return Operator != null && Operator.Spawned && Operator.Map == Map && !Operator.Downed && !Operator.Dead && Operator.Awake() &&
            Operator.health.capacities.CapableOf(PawnCapacityDefOf.Consciousness);
    }

    private bool PilotAtControls()
    {
        return OperatorAvailable() && Operator.Position == HomeAnchor && HasBoundKit() &&
            DroneControl.IsPiloting(Operator, this);
    }

    private void ReleasePilot()
    {
        if (Operator != null && DroneControl.IsPiloting(Operator, this))
        {
            Operator.jobs.EndCurrentJob(JobCondition.Succeeded);
        }
    }

    public void FinishFlight(bool hit)
    {
        CompleteFlight(hit, false, hit ? "FPV_EndHit" : IsScout ? "FPV_EndLanded" : "FPV_EndCommand");
    }

    private void CompleteFlight(bool hit, bool earlyReturn, string reason = "FPV_EndReturned", IntVec3? impactCell = null)
    {
        if (!flying || !Spawned)
        {
            return;
        }

        flying = false;
        Map map = Map;
        IntVec3 cell = impactCell ?? (LastFree.IsValid ? LastFree : Position);
        bool showReason = Faction == Faction.OfPlayer || !cell.Fogged(map);
        string aircraftLabel = LabelShortCap;
        ReleasePilot();
        if (!IsScout)
        {
            DroneControl.Blast(this, cell);
        }

        if (!Destroyed)
        {
            Destroy(DestroyMode.Vanish);
        }

        if (IsScout && !hit)
        {
            Thing item = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("FPV_Scout"));
            item.TryGetComp<CompDroneBattery>().Charge01 = Charge01;
            bool placedItem = GenPlace.TryPlaceThing(item, cell, map, ThingPlaceMode.Direct, out Thing placed) ||
                GenPlace.TryPlaceThing(item, cell, map, ThingPlaceMode.Near, out placed);
            if (placedItem && (earlyReturn || Returning && placed.Position != returnPoint))
            {
                Messages.Message("FPV_ReturnLandedEarly".Translate(), placed, MessageTypeDefOf.NeutralEvent);
            }
        }

        if (showReason)
            Messages.Message(reason.Translate(aircraftLabel), new TargetInfo(cell, map), MessageTypeDefOf.NeutralEvent);
    }

    protected override void Tick()
    {
        base.Tick();
        if (!flying || !Spawned)
        {
            return;
        }

        int now = Find.TickManager.TicksGame;
        if (batteryTicks <= 0)
        {
            // A direct collision in the depletion tick takes precedence over landing.
            if (now > depletedTick)
            {
                CompleteFlight(false, Returning && LastFree != landingTarget, "FPV_EndDepleted");
            }

            return;
        }

        if (!Returning && !PilotAtControls())
        {
            CompleteFlight(false, false, "FPV_EndPilotLost");
            return;
        }

        if (DroneJamming.IsJammed(this))
        {
            CompleteFlight(false, Returning && LastFree != landingTarget, "FPV_EndJammed");
            return;
        }

        batteryTicks--;
        if (batteryTicks == 0)
        {
            depletedTick = now;
            return;
        }

        UpdateAttackOrder(now);
        if (!flying) return;
        AdvanceFlight(now);
        if (flying && IsScout && !Returning)
        {
            CheckReturnWarning(now);
        }
    }

    private int TicksNeeded(float pathDistance)
    {
        float inProgressDistance = segmentTo.IsValid ? Distance(segmentFrom, segmentTo) - segmentTravel : 0f;
        return Mathf.CeilToInt((pathDistance + inProgressDistance) / CellDistancePerTick);
    }

    private void CheckReturnWarning(int now)
    {
        if (returnWarningSent || now < nextWarningCheck)
        {
            return;
        }

        nextWarningCheck = now + RouteRetryTicks;
        IntVec3 start = segmentTo.IsValid ? segmentTo : Position;
        if (TryFindLandingPath(start, HomeAnchor, false, out _, out _, out float distance) &&
            batteryTicks <= TicksNeeded(distance) + ReturnWarningTicks)
        {
            returnWarningSent = true;
            Messages.Message("FPV_ReturnWarning".Translate(), this, MessageTypeDefOf.CautionInput, false);
        }
    }

    private void AdvanceFlight(int now)
    {
        float budget = CellDistancePerTick;
        while (budget > MovementEpsilon && flying)
        {
            if (segmentTo.IsValid && (strikingWall ? !IsWallTransitCell(segmentTo) : !CanTravelStep(segmentFrom, segmentTo, CrossingWall)))
            {
                ClearSegment();
                ClearPath();
                nextRouteCheck = 0;
            }

            if (!segmentTo.IsValid && !BeginNextStep(now))
            {
                return;
            }

            float distance = Distance(segmentFrom, segmentTo);
            float usedDistance = MathF.Min(budget, distance - segmentTravel);
            segmentTravel += usedDistance;
            budget -= usedDistance;

            if (!strikingWall && !CrossingWall && segmentTravel * 2 >= distance)
            {
                ReachFreeCell(segmentTo);
            }

            if (segmentTravel + MovementEpsilon >= distance)
            {
                if (strikingWall)
                {
                    CompleteFlight(false, false, "FPV_EndWall", segmentTo);
                    return;
                }
                ReachFreeCell(segmentTo);
                ClearSegment();
                if (reacquirePending && Returning)
                {
                    TryReacquire();
                }
            }
        }
    }

    private void ReachFreeCell(IntVec3 cell)
    {
        if (Position == cell)
        {
            return;
        }

        Position = cell;
        LastFree = cell;
    }

    private bool BeginNextStep(int now)
    {
        while (flying)
        {
            if (pathIndex < path.Count)
            {
                IntVec3 next = path[pathIndex];
                if (CanTravelStep(Position, next, (Position - next).LengthHorizontalSquared == 4))
                {
                    pathIndex++;
                    SetSegment(next);
                    return true;
                }

                ClearPath();
            }

            if (Returning)
            {
                return BeginReturnStep();
            }

            if (waypoints.Count == 0)
            {
                return false;
            }

            if (Position == waypoints[0])
            {
                waypoints.RemoveAt(0);
                ClearPath();
                continue;
            }

            if (now < nextRouteCheck ||
                !TryFindPath(Position, waypoints[0], out List<IntVec3> route, out _))
            {
                routeBlocked = true;
                if (now >= nextRouteCheck)
                {
                    nextRouteCheck = now + RouteRetryTicks;
                }

                return false;
            }

            UsePath(route);
        }

        return false;
    }

    private bool BeginReturnStep()
    {
        if (Position == landingTarget && IsLandingCell(Position))
        {
            CompleteFlight(false, Position != returnPoint);
            return false;
        }

        if (!TryFindLandingPath(Position, returnPoint, true, out landingTarget, out List<IntVec3> route, out float distance) ||
            TicksNeeded(distance) > batteryTicks)
        {
            IntVec3 available = FindNearestLandingCell(Position);
            if (available != Position && TryFindPath(Position, available, out route, out distance) && TicksNeeded(distance) <= batteryTicks)
            {
                landingTarget = available;
            }
            else
            {
                CompleteFlight(false, true);
                return false;
            }
        }

        if (route.Count == 0)
        {
            CompleteFlight(false, Position != returnPoint);
            return false;
        }

        UsePath(route);
        SetSegment(path[pathIndex++]);
        return true;
    }

    private void SetSegment(IntVec3 to)
    {
        segmentFrom = Position;
        segmentTo = to;
        segmentTravel = 0f;
        IntVec3 offset = to - Position;
        Rotation = Math.Abs(offset.x) > Math.Abs(offset.z)
            ? (offset.x > 0 ? Rot4.East : Rot4.West)
            : (offset.z > 0 ? Rot4.North : Rot4.South);
    }

    private void ClearSegment()
    {
        segmentFrom = IntVec3.Invalid;
        segmentTo = IntVec3.Invalid;
        segmentTravel = 0f;
        strikingWall = false;
    }

    private void UsePath(List<IntVec3> route)
    {
        path = route;
        pathIndex = 0;
        routeBlocked = false;
        InvalidateRoutePreview();
    }

    private void ClearPath()
    {
        path.Clear();
        pathIndex = 0;
        InvalidateRoutePreview();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref Operator, "fpvOperator");
        Scribe_References.Look(ref boundGoggles, "fpvGoggles");
        Scribe_References.Look(ref boundController, "fpvController");
        Scribe_Values.Look(ref Returning, "fpvReturning");
        Scribe_Values.Look(ref LastFree, "fpvLastFree", IntVec3.Invalid);
        Scribe_Values.Look(ref HomeAnchor, "fpvHomeAnchor", IntVec3.Invalid);
        Scribe_Values.Look(ref pathIndex, "fpvPathIndex");
        Scribe_Values.Look(ref returnPoint, "fpvReturnPoint", IntVec3.Invalid);
        Scribe_Values.Look(ref landingTarget, "fpvLandingTarget", IntVec3.Invalid);
        Scribe_Values.Look(ref segmentFrom, "fpvSegmentFrom", IntVec3.Invalid);
        Scribe_Values.Look(ref segmentTo, "fpvSegmentTo", IntVec3.Invalid);
        Scribe_Values.Look(ref segmentTravel, "fpvSegmentTravel");
        Scribe_Values.Look(ref batteryTicks, "fpvBatteryTicks");
        Scribe_Values.Look(ref depletedTick, "fpvDepletedTick", -1);
        Scribe_Values.Look(ref nextRouteCheck, "fpvNextRouteCheck");
        Scribe_Values.Look(ref nextWarningCheck, "fpvNextWarningCheck");
        Scribe_Values.Look(ref returnWarningSent, "fpvReturnWarningSent");
        Scribe_Values.Look(ref reacquirePending, "fpvReacquirePending");
        Scribe_Values.Look(ref routeBlocked, "fpvRouteBlocked");
        Scribe_Values.Look(ref strikingWall, "fpvStrikingWall");
        Scribe_Values.Look(ref flying, "fpvFlying");
        Scribe_Collections.Look(ref waypoints, "fpvWaypoints", LookMode.Value);
        Scribe_Collections.Look(ref path, "fpvPath", LookMode.Value);
        ExposeAttackOrder();
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            waypoints ??= new List<IntVec3>();
            path ??= new List<IntVec3>();
        }
    }
}
