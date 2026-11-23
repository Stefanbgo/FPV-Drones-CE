using CombatExtended;
using RimWorld;
using Verse;
using Verse.AI;

namespace FPVDrones;

public sealed class RaidDroneInterception : MapComponent
{
    private const int ReactionInterval = 5;

    public RaidDroneInterception(Map map) : base(map) { }

    public override void MapComponentTick()
    {
        if (Find.TickManager.TicksGame % ReactionInterval != 0) return;

        var pawns = map.mapPawns.AllPawnsSpawned;
        bool hasAirborneDrone = false;
        foreach (Pawn pawn in pawns)
        {
            if (IsAvailableDrone(pawn))
            {
                hasAirborneDrone = true;
                break;
            }
        }
        if (!hasAirborneDrone) return;

        for (int index = pawns.Count - 1; index >= 0; index--)
            TryIntercept(pawns[index]);
    }

    private static bool IsAvailableDrone(Thing thing) =>
        thing is FlyingDrone { Spawned: true, FlightActive: true, CrossingWall: false };

    internal static void TryIntercept(Pawn pawn)
    {
        if (!pawn.Spawned || pawn.CurJob is not { playerForced: false, exitMapOnArrival: false } movement ||
            movement.def != JobDefOf.Goto || !pawn.pather.Moving ||
            !pawn.RaceProps.Humanlike || pawn.Faction == null || pawn.Faction == Faction.OfPlayer ||
            pawn.DeadOrDowned || pawn.InMentalState || !pawn.Awake() || pawn.stances.FullBodyBusy ||
            !pawn.jobs.IsCurrentJobPlayerInterruptible() || pawn.WorkTagIsDisabled(WorkTags.Violent) ||
            pawn.TryGetComp<CompSuppressable>() is { isSuppressed: true })
            return;

        var duty = pawn.mindState.duty?.def;
        if (duty != DutyDefOf.AssaultColony && duty != DutyDefOf.AssaultThing && duty != DutyDefOf.Sapper && duty != DutyDefOf.Breaching)
            return;

        var weapon = pawn.equipment?.Primary;
        if (weapon?.def.IsRangedWeapon != true || pawn.IsPsychologicallyInvisible() || RaidDroneOperators.ShouldPrioritizeLaunch(pawn) ||
            weapon.TryGetComp<CompAmmoUser>() is { CanBeFiredNow: false } ||
            pawn.CurrentEffectiveVerb is not { verbProps.Ranged: true } verb || !verb.Available())
            return;

        var flags = TargetScanFlags.NeedLOSToAll | TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable;
        if (verb.IsIncendiary_Ranged()) flags |= TargetScanFlags.NeedNonBurning;
        var target = AttackTargetFinder.BestShootTargetFromCurrentPosition(pawn, flags,
            thing => IsAvailableDrone(thing) && verb.CanHitTargetFrom(pawn.Position, thing));
        if (target == null || !verb.CanHitTargetFrom(pawn.Position, target.Thing)) return;

        // A normal ranged stance pauses the path follower and lets the same job resume afterward.
        if (!pawn.TryStartAttack(target.Thing)) return;
        pawn.mindState.enemyTarget = target.Thing;
        pawn.mindState.lastEngageTargetTick = Find.TickManager.TicksGame;
    }
}
