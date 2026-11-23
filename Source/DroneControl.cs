using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FPVDrones;

[StaticConstructorOnStartup]
internal static class InputPatches
{
    static InputPatches() => new Harmony("stef.fpvdrones.controls").PatchAll(typeof(InputPatches).Assembly);
}

public static class DroneControl
{
    internal static JobDef PilotJob => DefDatabase<JobDef>.GetNamed("FPV_Pilot");
    internal static bool IsDroneItem(Thing t) => DroneVariants.IsDroneItem(t);
    internal static Thing EquippedController(Pawn pawn)
    {
        var controller = pawn?.equipment?.Primary;
        return controller?.def.defName == "FPV_Controller" &&
            controller.TryGetComp<CompKitBinding>()?.AvailableTo(pawn) == true ? controller : null;
    }

    public static bool HasKit(Pawn pawn) => EquippedController(pawn) != null;

    public static bool IsPiloting(Pawn pawn, FlyingDrone drone) =>
        pawn?.CurJobDef == PilotJob && pawn.CurJob.targetB.Thing == drone;

    internal static bool ManualPilot(Pawn pawn) =>
        pawn?.CurJobDef == PilotJob && pawn.CurJob.targetB.Thing is FlyingDrone { Returning: false, FlightActive: true };

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
    private static class ProtectPilotMove
    {
        private static bool Prefix(Pawn ___pawn, Job job)
        {
            if (job?.def != JobDefOf.Goto || !ManualPilot(___pawn)) return true;
            Messages.Message("FPV_UI_OperatorBusy".Translate(), ___pawn, MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }

    public static bool StartPiloting(Pawn pawn, FlyingDrone drone)
    {
        if (pawn?.Spawned != true || pawn.DeadOrDowned || pawn.InMentalState || !HasKit(pawn)) return false;
        if (!pawn.jobs.IsCurrentJobPlayerInterruptible() || pawn.CurJobDef?.forceCompleteBeforeNextJob == true) return false;
        var hold = JobMaker.MakeJob(PilotJob, pawn.Position, drone);
        hold.playerForced = true;
        pawn.jobs.ClearQueuedJobs();
        pawn.jobs.StartJob(hold, JobCondition.InterruptForced, tag: JobTag.Misc);
        if (!IsPiloting(pawn, drone)) return false;
        drone.BindKit(pawn);
        return true;
    }

    internal static FlyingDrone ActiveDrone(Pawn pawn) => pawn.Map?.mapPawns.AllPawnsSpawned
        .OfType<FlyingDrone>().FirstOrDefault(x => x.Operator == pawn);

    public static FlyingDrone Launch(Pawn pawn, Thing item)
    {
        if (pawn?.Spawned != true || pawn.DeadOrDowned || pawn.InMentalState || pawn.Faction == Faction.OfPlayer && !pawn.Drafted ||
            !HasKit(pawn) || !IsDroneItem(item) || !pawn.inventory.innerContainer.Contains(item) ||
            ActiveDrone(pawn) != null) return null;
        var charge = item.TryGetComp<CompDroneBattery>()?.Charge01 ?? 0f;
        if (charge <= 0f) return null;
        var kind = DroneVariants.AirKindFor(item);
        var drone = (FlyingDrone)PawnGenerator.GeneratePawn(kind, pawn.Faction);
        GenSpawn.Spawn(drone, pawn.Position, pawn.Map);
        drone.BeginFlight(pawn, charge);
        drone.BindKit(pawn);
        if (pawn.CurJobDef == PilotJob && pawn.CurJob.targetB.Thing == item)
        {
            pawn.CurJob.targetB = drone;
        }
        else if (!StartPiloting(pawn, drone))
        {
            drone.Destroy();
            return null;
        }
        item.Destroy();
        if (pawn.Faction == Faction.OfPlayer) CameraJumper.TryJumpAndSelect(drone);
        return drone;
    }

    internal static void ChooseLaunchCell(Pawn pawn, Thing item)
    {
        int radius = item.def.defName == "FPV_Scout" ? 120 : 100;
        Find.Targeter.BeginTargeting(TargetingParameters.ForCell(),
            target => pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(PilotJob, target.Cell, item)),
            target => DrawRange(target.Cell, radius, pawn.Map),
            target => target.Cell.InBounds(pawn.Map) && target.Cell.Standable(pawn.Map)
                && pawn.CanReach(target.Cell, PathEndMode.OnCell, Danger.Deadly), pawn);
    }

    internal static void DrawRange(IntVec3 center, int radius, Map map)
    {
        if (map != Find.CurrentMap || !center.IsValid) return;
        const int segments = 128;
        Vector3 origin = center.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
        var previous = origin + new Vector3(radius, 0, 0);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            var next = origin + new Vector3(radius * Mathf.Cos(angle), 0, radius * Mathf.Sin(angle));
            if (previous.ToIntVec3().InBounds(map) || next.ToIntVec3().InBounds(map))
                GenDraw.DrawLineBetween(previous, next);
            previous = next;
        }
    }

    internal static Command_Action Command(string label, string description, Action action, Texture2D icon) =>
        new() { defaultLabel = label, defaultDesc = description, action = action, icon = icon };

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    private static class LaunchGizmos
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (var gizmo in __result) yield return gizmo;
            var p = __instance;
            if (!p.IsColonistPlayerControlled || !p.Drafted) yield break;
            var active = ActiveDrone(p);
            if (active != null)
            {
                yield return Command("FPV_UI_ChooseDrone".Translate(), "FPV_UI_ChooseDroneDesc".Translate(),
                    () => CameraJumper.TryJumpAndSelect(active), active.ControlIcon);
                if (ManualPilot(p))
                {
                    yield return Command("FPV_UI_StopPiloting".Translate(), "FPV_UI_StopPilotingDesc".Translate(),
                        () =>
                        {
                            void Stop()
                            {
                                if (active.Spawned && IsPiloting(p, active)) active.FinishFlight(false);
                            }
                            if (active.IsScout) Stop();
                            else Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("FPV_UI_ConfirmStop".Translate(), Stop));
                        }, active.ControlIcon);
                }
                yield break;
            }
            if (!HasKit(p)) yield break;
            foreach (var item in p.inventory.innerContainer.Where(IsDroneItem))
            {
                float charge = item.TryGetComp<CompDroneBattery>().Charge01;
                var command = Command("FPV_UI_Launch".Translate(item.LabelNoCount, item.thingIDNumber, charge.ToStringPercent()),
                    "FPV_UI_LaunchDesc".Translate(),
                    () => ChooseLaunchCell(p, item), item.def.uiIcon);
                command.onHover = () => DrawRange(p.Position, item.def.defName == "FPV_Scout" ? 120 : 100, p.Map);
                if (charge <= 0f) command.Disable("FPV_UI_Empty".Translate());
                command.groupable = false;
                yield return command;
            }
        }
    }

    [HarmonyPatch(typeof(Selector), "HandleMapClicks")]
    private static class FlightClicks
    {
        private static bool Prefix(Selector __instance)
        {
            var ev = Event.current;
            Map map = Find.CurrentMap;
            if (map == null) return true;
            var formation = map.GetComponent<DroneFormation>();
            if (formation.ProcessInput(__instance, ev)) return false;
            if (ev.type != EventType.MouseDown || ev.button != 1 ||
                !__instance.SelectedObjects.OfType<FlyingDrone>().Any(DroneFlightOrders.IsPlayerFlight)) return true;
            if (DroneGroups.IsPointerInRoster(map, ev.mousePosition))
            {
                ev.Use();
                return false;
            }
            var cell = UI.MouseCell();
            var target = DroneFlightOrders.TargetAt(cell, map);
            if (!ev.shift && target == null && formation.TryBegin(__instance, cell))
            {
                ev.Use();
                return false;
            }
            DroneFlightOrders.Issue(__instance, cell, target, ev.shift);
            if (__instance.SelectedObjects.Any(selected => selected is not FlyingDrone)) return true;
            ev.Use();
            return false;
        }
    }

    public static void Blast(FlyingDrone drone, IntVec3 cell) => DronePayload.Detonate(drone, cell);
    public static void DrawBlast(FlyingDrone drone) => DronePayload.DrawPreview(drone);
}

public class JobDriver_Pilot : JobDriver
{
    public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOn(() => pawn.DeadOrDowned || pawn.InMentalState || !DroneControl.HasKit(pawn));
        yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
        yield return Toils_General.Do(() =>
        {
            if (job.targetB.Thing is not FlyingDrone && DroneControl.Launch(pawn, job.targetB.Thing) == null)
                EndJobWith(JobCondition.Incompletable);
        });
        var hold = ToilMaker.MakeToil("PilotDrone");
        var seated = DefDatabase<AnimationDef>.GetNamed("FPV_SeatedPilot");
        hold.initAction = () =>
        {
            pawn.pather.StopDead();
            pawn.Drawer.renderer.SetAnimation(seated);
        };
        hold.tickIntervalAction = _ =>
        {
            if (!pawn.Drawer.renderer.HasAnimation) pawn.Drawer.renderer.SetAnimation(seated);
        };
        hold.AddFinishAction(() =>
        {
            if (pawn.Drawer.renderer.CurAnimation == seated) pawn.Drawer.renderer.SetAnimation(null);
        });
        hold.defaultCompleteMode = ToilCompleteMode.Never;
        hold.AddEndCondition(() => job.targetB.Thing is FlyingDrone { Spawned: true, Returning: false }
            && pawn.Position == job.targetA.Cell ? JobCondition.Ongoing : JobCondition.Succeeded);
        yield return hold;
    }
}



