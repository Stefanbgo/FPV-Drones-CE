using CombatExtended;
using RimWorld;
using Verse;

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
        if (!drone.IsHashIntervalTick(SuppressionInterval) ||
            !drone.Spawned || !drone.FlightActive || drone.IsScout || drone.CrossingWall || drone.Faction == null)
            return;

        var origin = drone.Position;
        var map = drone.Map;
        foreach (var thing in GenRadial.RadialDistinctThingsAround(origin, map, SuppressionRadius, useCenter: true))
        {
            if (thing is not Pawn pawn || !pawn.RaceProps.Humanlike || pawn.DeadOrDowned || !pawn.Awake() ||
                pawn.equipment?.Primary?.def.IsRangedWeapon == true || pawn.CurJobDef == JobDefOf.ManTurret ||
                pawn.Faction == null || !pawn.Faction.HostileTo(drone.Faction))
                continue;

            var suppression = pawn.TryGetComp<CompSuppressable>();
            if (suppression == null || !suppression.CanReactToSuppression ||
                !GenSight.LineOfSight(origin, pawn.Position, map, skipFirstCell: true) ||
                suppression.IgnoreSuppresion(origin))
                continue;

            bool close = origin.DistanceToSquared(pawn.Position) <= CloseSuppressionRadius * CloseSuppressionRadius;
            if (!close && suppression.CurrentSuppression >= suppression.SuppressionThreshold * 2f + ApproachSuppression)
                continue;

            suppression.AddSuppression(close ? CloseSuppression : ApproachSuppression, origin);
        }
    }
}
