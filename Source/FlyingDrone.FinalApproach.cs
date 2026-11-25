using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace FPVDrones;

public partial class FlyingDrone
{
    private const float FinalApproachRange = 3f;
    private const int FinalApproachTiltTicks = 6;
    private const int FinalApproachTimeoutTicks = 60;
    private const int FinalApproachRouteRetryTicks = 5;

    private static readonly SimpleCurve AttackChanceByBodySize = new()
    {
        new CurvePoint(0f, 0.05f),
        new CurvePoint(0.2f, 0.20f),
        new CurvePoint(1f, 0.60f),
        new CurvePoint(3f, 0.90f),
        new CurvePoint(8f, 0.97f)
    };

    private static AnimationDef finalApproachAnimation;
    private IntVec3 finalApproachCell = IntVec3.Invalid;
    private IntVec3 finalApproachImpact = IntVec3.Invalid;
    private bool finalApproachHit;
    private int finalApproachStartTick = -1;
    private int nextFinalApproachCheck;

    internal bool FinalApproachActive => finalApproachCell.IsValid;

    internal float FinalApproachTiltAt(int now) => FinalApproachActive
        ? Mathf.Clamp01((now - finalApproachStartTick) / (float)FinalApproachTiltTicks)
        : 0f;

    private static float AttackHitChance(Thing target)
    {
        if (DroneFlightOrders.IsMultiCellPawn(target))
        {
            var hull = target.OccupiedRect();
            return Mathf.Lerp(0.90f, 0.97f, Mathf.InverseLerp(1f, 15f, hull.Width * hull.Height));
        }

        return AttackChanceByBodySize.Evaluate(((Pawn)target).BodySize);
    }

    private static bool RollAttackHit(Thing target) => Rand.Chance(AttackHitChance(target));

    private bool TryCommitFinalApproach(Pawn target, int now)
    {
        if (CrossingWall || now < nextFinalApproachCheck)
            return false;

        var occupied = target.OccupiedRect();
        if (!TryPlanFinalContact(occupied, out var impact, out var destination, out var route))
        {
            nextFinalApproachCheck = now + FinalApproachRouteRetryTicks;
            return false;
        }

        finalApproachHit = RollAttackHit(target);
        if (!finalApproachHit)
        {
            if (segmentTo.IsValid && occupied.Contains(segmentTo))
                ClearSegment();
            IntVec3 start = segmentTo.IsValid ? segmentTo : Position;
            if (!TryPlanMiss(start, impact, occupied, out destination, out route))
            {
                destination = start;
                route = null;
            }
        }

        finalApproachStartTick = now;
        SetFinalApproachRoute(impact, destination, route);
        EnsureFinalApproachAnimation(now);
        return true;
    }

    private bool TryPlanFinalContact(CellRect occupied, out IntVec3 impact, out IntVec3 destination, out List<IntVec3> route)
    {
        Vector3 drawPos = DrawPos;
        impact = new IntVec3(
            Mathf.Clamp(Mathf.FloorToInt(drawPos.x), occupied.minX, occupied.maxX), 0,
            Mathf.Clamp(Mathf.FloorToInt(drawPos.z), occupied.minZ, occupied.maxZ));
        destination = impact;
        route = null;
        Vector3 offset = impact.ToVector3Shifted() - drawPos;
        if (offset.x * offset.x + offset.z * offset.z > FinalApproachRange * FinalApproachRange)
            return false;

        IntVec3 start = segmentTo.IsValid ? segmentTo : Position;
        if (TryFindPath(start, impact, out route, out float distance) && distance <= FinalApproachRange + 1f)
            return true;

        // A vehicle can overhang impassable terrain. Reach its edge as in the original hull attack.
        if (attackApproach == attackImpact || !occupied.Contains(attackImpact) || occupied.Contains(attackApproach) ||
            !TryFindPath(start, attackApproach, out route, out distance) || distance > FinalApproachRange + 1f)
            return false;

        impact = attackImpact;
        destination = attackApproach;
        return true;
    }

    private void SetFinalApproachRoute(IntVec3 impact, IntVec3 destination, List<IntVec3> route)
    {
        finalApproachCell = destination;
        finalApproachImpact = impact;
        AssignWaypoint(destination, false);
        if (route is { Count: > 0 })
            UsePath(route);
    }

    private bool TryPlanMiss(IntVec3 start, IntVec3 impact, CellRect occupied, out IntVec3 destination,
        out List<IntVec3> route)
    {
        Vector3 towardTarget = impact.ToVector3Shifted() - DrawPos;
        var adjacent = StepDirections
            .Where(offset => IsFreeFlightCell(impact + offset) && IsInPilotingRange(impact + offset) && !occupied.Contains(impact + offset))
            .ToArray();
        var sidewaysOrBeyond = adjacent
            .Where(offset => offset.x * towardTarget.x + offset.z * towardTarget.z >= -0.01f)
            .Select(offset => impact + offset)
            .ToArray();
        if (TryFindPath(start, sidewaysOrBeyond, out destination, out route, out float distance, avoidDrones: true, forbidden: occupied) &&
            distance <= FinalApproachRange + 2f)
            return true;

        return TryFindPath(start, adjacent.Select(offset => impact + offset).ToArray(), out destination, out route,
            out distance, avoidDrones: true, forbidden: occupied) && distance <= FinalApproachRange + 2f;
    }

    private CellRect? FinalMissObstacle()
    {
        if (!FinalApproachActive || finalApproachHit || assignedAttackTarget is not Pawn { Spawned: true } target)
            return null;

        var bounds = target.OccupiedRect();
        return bounds.Contains(finalApproachCell) ? null : bounds;
    }

    private void UpdateFinalApproach(int now)
    {
        EnsureFinalApproachAnimation(now);
        if (now - finalApproachStartTick >= FinalApproachTimeoutTicks)
        {
            CompleteFlight(false, false, "FPV_EndAttackMiss");
            return;
        }

        FollowFinalApproachTarget(now);
        if (!segmentTo.IsValid && Position == finalApproachCell)
            FinishFinalApproach(now);
    }

    private void FollowFinalApproachTarget(int now)
    {
        if (!finalApproachHit || CrossingWall || now < nextFinalApproachCheck || assignedAttackTarget is not Pawn target ||
            !DroneFlightOrders.IsAttackTarget(target, Map))
            return;

        var occupied = target.OccupiedRect();
        if (occupied.Contains(finalApproachImpact))
            return;

        nextFinalApproachCheck = now + FinalApproachRouteRetryTicks;
        if (TryPlanFinalContact(occupied, out var impact, out var destination, out var route))
            SetFinalApproachRoute(impact, destination, route);
    }

    private void FinishFinalApproach(int now)
    {
        if (now - finalApproachStartTick < FinalApproachTiltTicks)
            return;

        if (finalApproachHit && assignedAttackTarget is Pawn target && DroneFlightOrders.IsAttackTarget(target, Map) &&
            target.OccupiedRect().Contains(finalApproachImpact))
            CompleteFlight(false, false, "FPV_EndAttack", finalApproachImpact, target);
        else
            CompleteFlight(false, false, "FPV_EndAttackMiss", finalApproachCell);
    }

    private void EnsureFinalApproachAnimation(int now)
    {
        finalApproachAnimation ??= DefDatabase<AnimationDef>.GetNamed("FPV_FinalApproach");
        if (now - finalApproachStartTick < finalApproachAnimation.durationTicks &&
            Drawer.renderer.CurAnimation != finalApproachAnimation)
            Drawer.renderer.SetAnimation(finalApproachAnimation);
    }

    private void ClearFinalApproach()
    {
        finalApproachCell = finalApproachImpact = IntVec3.Invalid;
        finalApproachHit = false;
        finalApproachStartTick = -1;
        nextFinalApproachCheck = 0;
        if (finalApproachAnimation != null && Drawer.renderer.CurAnimation == finalApproachAnimation)
            Drawer.renderer.SetAnimation(null);
    }

    private void ExposeFinalApproach()
    {
        Scribe_Values.Look(ref finalApproachCell, "fpvFinalApproachCell", IntVec3.Invalid);
        Scribe_Values.Look(ref finalApproachImpact, "fpvFinalApproachImpact", IntVec3.Invalid);
        Scribe_Values.Look(ref finalApproachHit, "fpvFinalApproachHit");
        Scribe_Values.Look(ref finalApproachStartTick, "fpvFinalApproachStartTick", -1);
    }
}
