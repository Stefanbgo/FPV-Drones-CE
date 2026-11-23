using CombatExtended;
using RimWorld;
using Verse;
using Verse.AI;

namespace FPVDrones;

internal static class DroneCombatResponse
{
    private const int SuppressionInterval = 10;
    private const int SuppressionRadius = 8;
    private const int CloseSuppressionRadius = 3;
    private const float ApproachSuppression = 25f;
    private const float CloseSuppression = 525f;

    internal static void ApplyNearbySuppression(FlyingDrone drone)
    {
        if (!drone.Spawned || !drone.FlightActive || drone.IsScout || drone.CrossingWall || drone.Faction == null)
            return;

        var origin = drone.Position;
        var map = drone.Map;
        int radius = drone.IsHashIntervalTick(SuppressionInterval) ? SuppressionRadius : CloseSuppressionRadius;
        foreach (var thing in GenRadial.RadialDistinctThingsAround(origin, map, radius, useCenter: true))
        {
            if (thing is not Pawn pawn || !pawn.RaceProps.Humanlike || pawn.DeadOrDowned || !pawn.Awake() ||
                pawn.equipment?.Primary?.def.IsRangedWeapon == true || pawn.CurJobDef == JobDefOf.ManTurret ||
                pawn.Faction == null || !pawn.Faction.HostileTo(drone.Faction))
                continue;

            var suppression = pawn.TryGetComp<CompSuppressable>();
            if (suppression == null || !GenSight.LineOfSight(origin, pawn.Position, map, skipFirstCell: true) ||
                suppression.IgnoreSuppresion(origin))
                continue;

            if (origin.DistanceToSquared(pawn.Position) <= CloseSuppressionRadius * CloseSuppressionRadius)
            {
                HunkerImmediately(pawn, suppression, origin);
                continue;
            }

            if (suppression.CanReactToSuppression && suppression.CurrentSuppression < suppression.SuppressionThreshold * 2f + ApproachSuppression)
                suppression.AddSuppression(ApproachSuppression, origin);
        }
    }

    private static void HunkerImmediately(Pawn pawn, CompSuppressable suppression, IntVec3 origin)
    {
        if (pawn.CurJobDef == CE_JobDefOf.HunkerDown && suppression.isSuppressed)
        {
            pawn.jobs.posture = PawnPosture.LayingOnGroundNormal;
            return;
        }

        float susceptibility = pawn.GetStatValue(CE_StatDefOf.Suppressability);
        if (!float.IsFinite(susceptibility) || susceptibility <= 0f || pawn.InMentalState)
            return;

        if (pawn.stances?.curStance is Stance_Busy { verb.IsMeleeAttack: true })
            pawn.stances.CancelBusyStanceHard();
        if (!suppression.CanReactToSuppression)
            return;

        suppression.AddSuppression(CloseSuppression / susceptibility, origin);
        if (!suppression.isSuppressed)
            return;

        if (pawn.CurJobDef != CE_JobDefOf.HunkerDown)
            pawn.jobs.StartJob(JobMaker.MakeJob(CE_JobDefOf.HunkerDown, pawn), JobCondition.InterruptForced);

        // CE's job handles getting back up. Skip its initial delay for a close flyby.
        if (pawn.CurJobDef == CE_JobDefOf.HunkerDown)
            pawn.jobs.posture = PawnPosture.LayingOnGroundNormal;
    }
}
