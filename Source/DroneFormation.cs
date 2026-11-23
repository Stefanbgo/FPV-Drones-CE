using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FPVDrones;

public sealed class DroneFormation : MapComponent
{
    private readonly List<FlyingDrone> drones = new();
    private readonly List<(FlyingDrone Drone, IntVec3 Cell)> destinations = new();
    private IntVec3 start = IntVec3.Invalid;
    private IntVec3 end;

    public DroneFormation(Map map) : base(map) { }

    internal bool TryBegin(Selector selector, IntVec3 cell)
    {
        if (!cell.InBounds(map) || selector.SelectedObjects.Count < 2 ||
            selector.SelectedObjects.Any(thing => thing is not FlyingDrone drone || !DroneFlightOrders.IsPlayerFlight(drone)))
            return false;

        drones.Clear();
        drones.AddRange(selector.SelectedObjects.OfType<FlyingDrone>().Where(drone => !drone.Returning && drone.ChargeTicks > 0));
        if (drones.Count < 2) return false;

        start = end = cell;
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

        if (input.rawType == EventType.MouseUp && input.button == 1)
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
        foreach (var (_, cell) in destinations)
            GenDraw.DrawCircleOutline(cell.ToVector3ShiftedWithAltitude(layer), 0.43f, SimpleColor.Green);
    }

    private bool SelectionStillValid(Selector selector) =>
        drones.All(drone => DroneFlightOrders.IsPlayerFlight(drone) && drone.Map == map && selector.IsSelected(drone));

    private void Finish(Selector selector, IntVec3 last)
    {
        if (last == start)
        {
            DroneFlightOrders.Issue(selector, last, null, false);
        }
        else
        {
            Plan(last, true);
            foreach (var (drone, cell) in destinations)
                drone.SetWaypoint(cell, false);
            if (destinations.Count != drones.Count)
                Messages.Message("FPV_FormationUnavailable".Translate(destinations.Count, drones.Count), MessageTypeDefOf.RejectInput, false);
        }
        Cancel();
    }

    private void Plan(IntVec3 last, bool checkRoute)
    {
        destinations.Clear();
        if (last == start) return;

        var used = new HashSet<IntVec3>();
        var line = (last - start).ToVector3();
        var ordered = drones.OrderBy(drone => Vector3.Dot(drone.Position.ToVector3(), line));
        int index = 0;
        foreach (var drone in ordered)
        {
            var position = start.ToVector3() + line * (index++ / (float)(drones.Count - 1));
            var midpoint = new IntVec3(Mathf.RoundToInt(position.x), 0, Mathf.RoundToInt(position.z));
            foreach (var cell in GenRadial.RadialCellsAround(midpoint, 2.9f, true))
            {
                if (used.Contains(cell) || !drone.AcceptsWaypoint(cell) || checkRoute && !drone.CanReachRaidCell(cell))
                    continue;
                used.Add(cell);
                destinations.Add((drone, cell));
                break;
            }
        }
    }

    private void Cancel()
    {
        start = IntVec3.Invalid;
        destinations.Clear();
        drones.Clear();
    }
}
