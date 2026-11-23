using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;

namespace FPVDrones;

public sealed class RaidDroneOperators : MapComponent
{
    private const int MaxOperators = 4;
    private const int MaxDronesPerOperator = 4;
    private const int MinimumRaidPoints = 1000;
    private const int PointsPerExtraOperator = 650;
    private const int ThinkInterval = 30;
    private const int DroneRangeSquared = 100 * 100;
    private const string ControllerName = "FPV_Controller";
    private static readonly string[] AlternateDrones = { "FPV_KamikazeFiber", "FPV_Incendiary", "FPV_IncendiaryFiber" };
    private List<RaidPilot> pilots = new();

    public RaidDroneOperators(Map map) : base(map) { }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref pilots, "fpvRaidPilots", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
            pilots ??= new List<RaidPilot>();
    }

    internal static bool AdvancedHostileFaction(Faction faction) =>
        faction != null && faction.def.humanlikeFaction && faction.def.techLevel >= TechLevel.Industrial &&
        faction.HostileTo(Faction.OfPlayer);

    internal void AssignRaidRoles(IncidentParms parms, List<Pawn> raiders)
    {
        if (parms.points < MinimumRaidPoints || !AdvancedHostileFaction(parms.faction)) return;

        int pilotLimit = Math.Min(MaxOperators, 1 + (int)(parms.points - MinimumRaidPoints) / PointsPerExtraOperator);
        int stock = Math.Min(MaxDronesPerOperator, 2 + (int)parms.points / 1800);
        var eligible = raiders.Where(pawn => pawn.Faction == parms.faction && pawn.RaceProps.Humanlike &&
            pawn.inventory != null && pawn.equipment?.Primary is { } primary && primary.def.IsWeapon).ToList();

        int assigned = 0;
        foreach (Pawn pawn in eligible)
        {
            if (assigned >= pilotLimit) break;
            if (pilots.Any(assignment => assignment.Pawn == pawn)) continue;
            if (!GiveItem(pawn, ControllerName)) continue;
            for (int index = 0; index < stock; index++)
            {
                string defName = index == 0 || index == 2 ? "FPV_Kamikaze" : AlternateDrones[(assigned + index) % AlternateDrones.Length];
                GiveItem(pawn, defName);
            }

            pilots.Add(new RaidPilot(pawn, pawn.equipment.Primary, Find.TickManager.TicksGame + 6000));
            assigned++;
        }

        if (assigned > 0 && parms.points >= 2200 && Rand.Chance(0.3f))
            TryAddJammer(eligible);
    }

    private static bool GiveItem(Pawn pawn, string defName)
    {
        Thing item = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(defName));
        if (pawn.inventory.innerContainer.TryAdd(item, false)) return true;
        item.Destroy();
        return false;
    }

    private static void TryAddJammer(List<Pawn> raiders)
    {
        var jammerDef = DefDatabase<ThingDef>.GetNamed("FPV_JammerBackpack", errorOnFail: false);
        if (jammerDef == null) return;
        foreach (Pawn pawn in raiders)
        {
            if (pawn.apparel == null || !ApparelUtility.HasPartsToWear(pawn, jammerDef) ||
                pawn.apparel.WornApparel.Any(item => !ApparelUtility.CanWearTogether(item.def, jammerDef, pawn.RaceProps.body)))
                continue;
            pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(jammerDef), dropReplacedApparel: false);
            return;
        }
    }

    public override void MapComponentTick()
    {
        if (pilots.Count == 0) return;
        int now = Find.TickManager.TicksGame;
        if (now % ThinkInterval != 0) return;
        for (int index = pilots.Count - 1; index >= 0; index--)
        {
            if (!TickPilot(pilots[index], now)) pilots.RemoveAt(index);
        }
    }

    private bool TickPilot(RaidPilot assignment, int now)
    {
        Pawn pilot = assignment.Pawn;
        if (pilot == null || pilot.Destroyed || pilot.Dead || !AdvancedHostileFaction(pilot.Faction)) return false;
        if (!pilot.Spawned || pilot.Map != map) return now < assignment.ArrivalDeadline;
        if (pilot.Downed || pilot.InMentalState || !pilot.Awake()) return true;

        if (DroneControl.ActiveDrone(pilot) is { } aircraft)
        {
            GuideDrone(assignment, aircraft);
            return true;
        }

        var stock = pilot.inventory.innerContainer.Where(item => DroneVariants.IsDroneItem(item) && item.def.defName != "FPV_Scout").ToList();
        if (stock.Count == 0)
        {
            EquipBackup(assignment);
            return pilot.equipment.Primary?.def.defName == ControllerName;
        }

        DutyDef duty = pilot.mindState?.duty?.def;
        if (duty != DutyDefOf.AssaultColony && duty != DutyDefOf.Breaching && duty != DutyDefOf.Sapper && duty != DutyDefOf.AssaultThing)
        {
            EquipBackup(assignment);
            return true;
        }

        var goal = SelectGoal(pilot, null, assignment.Objective);
        if (!goal.Cell.IsValid)
        {
            assignment.LaunchCell = IntVec3.Invalid;
            EquipBackup(assignment);
            return true;
        }

        Thing item = goal.Target is Building
            ? stock.FirstOrDefault(drone => !DroneVariants.IsIncendiary(drone.def)) ?? stock[0]
            : stock[0];
        var launchCell = FindLaunchCell(pilot, item.def, goal.Cell, assignment.LaunchCell);
        if (!launchCell.IsValid) return true;
        assignment.LaunchCell = launchCell;
        assignment.Objective = goal.Target;
        if (pilot.Position != launchCell)
        {
            if (pilot.CurJobDef != JobDefOf.Goto || pilot.CurJob.targetA.Cell != launchCell)
                pilot.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, launchCell), JobCondition.InterruptForced, tag: JobTag.Misc);
            return true;
        }

        if (!EquipController(pilot)) return true;
        if (DroneControl.Launch(pilot, item) is { } launched)
        {
            assignment.LaunchCell = IntVec3.Invalid;
            assignment.Waypoint = IntVec3.Invalid;
            GuideDrone(assignment, launched);
        }

        return true;
    }

    private void GuideDrone(RaidPilot assignment, FlyingDrone aircraft)
    {
        var goal = SelectGoal(assignment.Pawn, aircraft, assignment.Objective);
        assignment.Objective = goal.Target;
        if (!goal.Cell.IsValid || aircraft.CrossingWall) return;

        if (aircraft.Position != goal.Cell)
        {
            if (assignment.Waypoint != goal.Cell && aircraft.TrySetRaidWaypoint(goal.Cell))
                assignment.Waypoint = goal.Cell;
            return;
        }

        IntVec3 blastCell = goal.Target is Building ? goal.Target.Position : aircraft.Position;
        if (FriendlyInBlast(DronePayload.BlastCells(aircraft, blastCell), aircraft.Faction, aircraft)) return;
        if (goal.Target is Building) aircraft.EndRaidAtWall(blastCell);
        else aircraft.FinishFlight(false);
    }

    private (Thing Target, IntVec3 Cell) SelectGoal(Pawn pilot, FlyingDrone aircraft, Thing previous)
    {
        IntVec3 origin = aircraft?.Position ?? pilot.Position;
        IntVec3 anchor = aircraft?.HomeAnchor ?? pilot.Position;
        foreach (Pawn target in map.mapPawns.AllPawnsSpawned.Where(target => target.Faction == Faction.OfPlayer &&
            target is not FlyingDrone && !target.DeadOrDowned && target.Position.DistanceToSquared(anchor) <= DroneRangeSquared)
            .OrderBy(target => target != previous).ThenBy(target => target.Position.DistanceToSquared(origin)))
        {
            if (GenSight.LineOfSight(origin, target.Position, map, skipFirstCell: true) &&
                (aircraft == null || aircraft.CanReachRaidCell(target.Position))) return (target, target.Position);
        }

        var walls = map.listerBuildings.allBuildingsColonist.Where(wall => wall.Spawned && wall.def.IsWall &&
                wall.Position.DistanceToSquared(anchor) <= DroneRangeSquared)
            .OrderBy(wall => wall != previous).ThenBy(wall => wall.Position.DistanceToSquared(origin));
        foreach (Building wall in walls)
        {
            foreach (IntVec3 offset in GenAdj.CardinalDirections)
            {
                IntVec3 approach = wall.Position + offset;
                if (!approach.InBounds(map) || approach.Roofed(map) || !approach.Walkable(map) || approach.GetEdifice(map) != null ||
                    !GenSight.LineOfSight(origin, approach, map, skipFirstCell: true) ||
                    (aircraft != null && !aircraft.CanReachRaidCell(approach))) continue;
                return (wall, approach);
            }
        }

        return (null, IntVec3.Invalid);
    }

    private IntVec3 FindLaunchCell(Pawn pilot, ThingDef item, IntVec3 goal, IntVec3 previous)
    {
        if (LaunchCellSafe(pilot, item, goal, previous)) return previous;
        if (LaunchCellSafe(pilot, item, goal, pilot.Position)) return pilot.Position;
        foreach (IntVec3 cell in GenRadial.RadialCellsAround(pilot.Position, 8f, false))
        {
            if (LaunchCellSafe(pilot, item, goal, cell) && pilot.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return cell;
        }

        return IntVec3.Invalid;
    }

    private bool LaunchCellSafe(Pawn pilot, ThingDef item, IntVec3 goal, IntVec3 cell) =>
        cell.InBounds(map) && cell.Standable(map) && cell.GetEdifice(map) == null &&
        goal.DistanceToSquared(cell) <= DroneRangeSquared &&
        !FriendlyInBlast(DronePayload.BlastCells(item, cell, map), pilot.Faction, pilot);

    private bool FriendlyInBlast(IEnumerable<IntVec3> cells, Faction faction, Pawn ignored)
    {
        foreach (IntVec3 cell in cells)
        {
            if (!cell.InBounds(map)) continue;
            foreach (Thing thing in cell.GetThingList(map))
            {
                if (thing is Pawn pawn && pawn != ignored && pawn.Faction is { } owner &&
                    (owner == faction || owner.RelationKindWith(faction) == FactionRelationKind.Ally)) return true;
            }
        }

        return false;
    }

    private static bool EquipController(Pawn pawn)
    {
        if (DroneControl.HasKit(pawn)) return true;
        var controller = pawn.inventory.innerContainer.OfType<ThingWithComps>().FirstOrDefault(item => item.def.defName == ControllerName);
        return controller != null && EquipCarried(pawn, controller) && DroneControl.HasKit(pawn);
    }

    private static void EquipBackup(RaidPilot assignment)
    {
        Pawn pawn = assignment.Pawn;
        if (pawn.equipment.Primary?.def.defName != ControllerName) return;
        ThingWithComps backup = assignment.Backup;
        if (backup == null || !pawn.inventory.innerContainer.Contains(backup))
            backup = pawn.inventory.innerContainer.OfType<ThingWithComps>().FirstOrDefault(item => item.def.IsWeapon && item.def.defName != ControllerName);
        if (backup != null) EquipCarried(pawn, backup);
    }

    private static bool EquipCarried(Pawn pawn, ThingWithComps equipment)
    {
        ThingOwner inventory = pawn.inventory.innerContainer;
        if (!inventory.Contains(equipment)) return false;
        if (pawn.equipment.Primary is { } current && !pawn.equipment.TryTransferEquipmentToContainer(current, inventory)) return false;
        inventory.Remove(equipment);
        pawn.equipment.AddEquipment(equipment);
        return true;
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    private static class EquipRaidOnSpawn
    {
        private static void Postfix(IncidentWorker_Raid __instance, IncidentParms parms, List<Pawn> pawns, bool debugTest, bool __result)
        {
            if (__result && !debugTest && __instance is IncidentWorker_RaidEnemy && parms.target is Map map)
                map.GetComponent<RaidDroneOperators>()?.AssignRaidRoles(parms, pawns);
        }
    }

    [DebugAction("FPV Drones", "Test raid with FPV operators", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void TestRaid()
    {
        Faction faction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(AdvancedHostileFaction);
        if (faction == null)
        {
            Messages.Message("FPV_RaidNoFaction".Translate(), MessageTypeDefOf.RejectInput, false);
            return;
        }

        var parms = new IncidentParms
        {
            target = Find.CurrentMap,
            points = 3800,
            faction = faction,
            forced = true,
            raidStrategy = RaidStrategyDefOf.ImmediateAttack,
            raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn
        };
        if (!IncidentDefOf.RaidEnemy.Worker.TryExecute(parms))
            Messages.Message("FPV_RaidUnavailable".Translate(), MessageTypeDefOf.RejectInput, false);
    }

    private sealed class RaidPilot : IExposable
    {
        public Pawn Pawn;
        public ThingWithComps Backup;
        public Thing Objective;
        public IntVec3 LaunchCell = IntVec3.Invalid;
        public IntVec3 Waypoint = IntVec3.Invalid;
        public int ArrivalDeadline;

        public RaidPilot() { }

        public RaidPilot(Pawn pawn, ThingWithComps backup, int arrivalDeadline)
        {
            Pawn = pawn;
            Backup = backup;
            ArrivalDeadline = arrivalDeadline;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref Pawn, "pilot");
            Scribe_References.Look(ref Backup, "backup");
            Scribe_References.Look(ref Objective, "objective");
            Scribe_Values.Look(ref LaunchCell, "launchCell", IntVec3.Invalid);
            Scribe_Values.Look(ref Waypoint, "waypoint", IntVec3.Invalid);
            Scribe_Values.Look(ref ArrivalDeadline, "arrivalDeadline");
        }
    }
}
