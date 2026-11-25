using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FPVDrones;

public sealed class DroneFormation : MapComponent
{
    private const float SlotSearchRadius = 2.9f;

    private readonly List<Pawn> units = new();
    private readonly List<(Pawn Unit, IntVec3 Cell)> destinations = new();
    private IntVec3 start = IntVec3.Invalid;
    private IntVec3 end;
    private bool mixed;

    public DroneFormation(Map map) : base(map) { }

    internal bool TryBegin(Selector selector, IntVec3 cell)
    {
        if (!cell.InBounds(map) || selector.SelectedObjects.Count < 2)
            return false;

        units.Clear();
        units.AddRange(selector.SelectedObjects.OfType<Pawn>().Where(CanJoin));
        if (units.Count < 2 || !units.Any(unit => unit is FlyingDrone))
            return false;

        var ground = selector.SelectedObjects.OfType<Pawn>().Where(unit => unit is not FlyingDrone).ToList();
        if (ground.Count > 0 && FloatMenuMakerMap.GetOptions(ground, cell.ToVector3Shifted(), out _)
                .Any(option => !option.isGoto && !option.Disabled))
            return false;

        start = end = cell;
        mixed = units.Any(unit => unit is not FlyingDrone);
        return true;
    }

    internal bool ProcessInput(Selector selector, Event input)
    {
        if (!start.IsValid) return false;

        if (!SelectionStillValid(selector) || KeyBindingDefOf.Cancel.KeyDownEvent)
        {
            Cancel();
            return false;
        }

        if ((input.type == EventType.MouseUp || input.rawType == EventType.MouseUp) && input.button == 1)
        {
            IntVec3 cell = UI.MouseCell();
            if (input.type != EventType.Used && cell.InBounds(map) &&
                !DroneGroups.IsPointerInRoster(map, input.mousePosition))
                Finish(selector, cell);
            else
                Cancel();
            if (input.type != EventType.Used) input.Use();
            return true;
        }

        if (input.type == EventType.MouseDrag && input.button == 1)
        {
            IntVec3 cell = UI.MouseCell();
            if (cell.InBounds(map) && cell != end)
            {
                end = cell;
                Plan(end, false);
            }
            input.Use();
            return true;
        }

        if (input.type == EventType.MouseDown || input.type == EventType.Repaint && !Input.GetMouseButton(1))
            Cancel();
        return false;
    }

    public override void MapComponentDraw()
    {
        if (!start.IsValid || start == end || Find.CurrentMap != map)
            return;

        var layer = AltitudeLayer.MetaOverlays;
        GenDraw.DrawLineBetween(start.ToVector3ShiftedWithAltitude(layer), end.ToVector3ShiftedWithAltitude(layer), SimpleColor.Green, 0.13f);
        foreach (var (unit, cell) in destinations)
        {
            var color = mixed && unit is FlyingDrone ? SimpleColor.Cyan : SimpleColor.Green;
            GenDraw.DrawCircleOutline(cell.ToVector3ShiftedWithAltitude(layer), 0.43f, color);
        }
    }

    public override void MapComponentOnGUI()
    {
        if (!mixed || !start.IsValid || start == end || map != Find.CurrentMap) return;
        foreach (var (unit, cell) in destinations)
        {
            if (!unit.Spawned || cell.Fogged(map)) continue;
            Rect slot = cell.ToUIRect();
            GenMapUI.DrawPawnLabel(unit, new Vector2(slot.center.x, slot.yMax + 5f), 0.8f);
        }
    }

    private bool SelectionStillValid(Selector selector) => units.All(selector.IsSelected);

    private bool CanJoin(Pawn unit)
    {
        if (!unit.Spawned || unit.Map != map) return false;
        if (unit is FlyingDrone drone)
            return DroneFlightOrders.IsPlayerFlight(drone) && !drone.Returning && !drone.FinalApproachActive && drone.ChargeTicks > 0;
        return unit.CanTakeOrder && unit.Drafted && !unit.InMentalState && !DroneControl.ManualPilot(unit) &&
            FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(unit).Accepted;
    }

    private void Finish(Selector selector, IntVec3 last)
    {
        if (last == start && !mixed)
        {
            DroneFlightOrders.Issue(selector, last, null, false);
        }
        else
        {
            Plan(last, true);
            foreach (var (unit, cell) in destinations)
            {
                if (unit is FlyingDrone drone)
                    drone.SetWaypoint(cell, false);
                else
                    FloatMenuOptionProvider_DraftedMove.PawnGotoAction(last, unit, cell);
            }
            if (destinations.Count != units.Count)
            {
                string key = mixed ? "FPV_MixedFormationUnavailable" : "FPV_FormationUnavailable";
                Messages.Message(key.Translate(destinations.Count, units.Count), MessageTypeDefOf.RejectInput, false);
            }
        }
        Cancel();
    }

    private void Plan(IntVec3 last, bool checkRoute)
    {
        destinations.Clear();
        var used = new HashSet<IntVec3>();
        var line = (last - start).ToVector3();
        var ordered = units.OrderBy(unit => Vector3.Dot(unit.Position.ToVector3(), line));
        int index = 0;
        foreach (var unit in ordered)
        {
            var position = start.ToVector3() + line * (index++ / (float)(units.Count - 1));
            if (!CanJoin(unit)) continue;

            var midpoint = new IntVec3(Mathf.RoundToInt(position.x), 0, Mathf.RoundToInt(position.z));
            IntVec3 cell;
            if (unit is FlyingDrone drone)
            {
                cell = GenRadial.RadialCellsAround(midpoint, SlotSearchRadius, true).FirstOrDefault(
                    candidate => !used.Contains(candidate) && drone.AcceptsWaypoint(candidate) && (!checkRoute || drone.CanReachRaidCell(candidate)),
                    IntVec3.Invalid);
            }
            else
            {
                cell = RCellFinder.BestOrderedGotoDestNear(midpoint, unit, CanUseGroundSlot, reachable: false);
                if (!CanUseGroundSlot(cell)) continue;
            }
            if (!cell.IsValid) continue;

            used.Add(cell);
            destinations.Add((unit, cell));

            bool CanUseGroundSlot(IntVec3 candidate) => candidate.InBounds(map) && !used.Contains(candidate) &&
                (candidate - midpoint).LengthHorizontalSquared <= SlotSearchRadius * SlotSearchRadius &&
                FloatMenuOptionProvider_DraftedMove.PawnCanGoto(unit, candidate).Accepted;
        }
    }

    private void Cancel()
    {
        start = IntVec3.Invalid;
        destinations.Clear();
        units.Clear();
        mixed = false;
    }
}
