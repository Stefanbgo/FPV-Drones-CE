using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FPVDrones;

public sealed class DroneGroups : MapComponent
{
    private const int GroupCount = 4;
    private readonly List<FlyingDrone>[] groups = new List<FlyingDrone>[GroupCount];
    private static readonly KeyCode[] Keys = { KeyCode.Z, KeyCode.X, KeyCode.C, KeyCode.V };
    private readonly List<FlyingDrone> visibleFlights = new();
    private bool expanded;
    private Vector2 scroll;
    private Rect rosterBounds;

    public DroneGroups(Map map) : base(map)
    {
        for (int i = 0; i < GroupCount; i++) groups[i] = new();
    }

    private bool Eligible(FlyingDrone drone) => DroneFlightOrders.IsPlayerFlight(drone) && drone.Map == map;

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref expanded, "fpvDroneRoster");
        for (int i = 0; i < GroupCount; i++)
        {
            if (Scribe.mode == LoadSaveMode.Saving) groups[i].RemoveAll(d => !Eligible(d));
            Scribe_Collections.Look(ref groups[i], "fpvDroneGroup" + i, LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) groups[i] ??= new();
        }
    }

    internal IEnumerable<FlyingDrone> SelectedDrones =>
        Find.Selector.SelectedObjects.OfType<FlyingDrone>().Where(Eligible);

    internal void Assign(int index)
    {
        var selected = SelectedDrones.ToList();
        if (selected.Count == 0) return;
        groups[index] = selected;
        Messages.Message("FPV_GroupAssigned".Translate(index + 1, selected.Count), MessageTypeDefOf.TaskCompletion, false);
    }

    internal void Recall(int index)
    {
        groups[index].RemoveAll(d => !Eligible(d));
        if (groups[index].Count == 0) return;
        Find.Selector.ClearSelection();
        foreach (var drone in groups[index]) Find.Selector.Select(drone, false);
    }

    public static void SelectFromBadge(FlyingDrone drone)
    {
        if (!DroneFlightOrders.IsPlayerFlight(drone)) return;
        if (Event.current?.shift == true && Find.Selector.IsSelected(drone))
            Find.Selector.Deselect(drone);
        else
        {
            if (Event.current?.shift != true) Find.Selector.ClearSelection();
            Find.Selector.Select(drone);
        }
    }

    public override void MapComponentOnGUI()
    {
        rosterBounds = default;
        if (map != Find.CurrentMap || Find.WindowStack.AnyWindowAbsorbingAllInput) return;
        visibleFlights.Clear();
        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            if (pawn is FlyingDrone drone && Eligible(drone)) visibleFlights.Add(drone);
        if (visibleFlights.Count == 0) return;

        var ev = Event.current;
        if (ev.type == EventType.KeyDown && ev.alt)
        {
            int i = System.Array.IndexOf(Keys, ev.keyCode);
            if (i >= 0)
            {
                if (ev.control) Assign(i); else Recall(i);
                ev.Use();
            }
        }

        float width = expanded ? 218f : 138f;
        float x = UI.screenWidth - width - 12f;
        const float top = 86f;
        float y = top;
        float overlayLeft = UI.screenWidth;
        var windows = Find.WindowStack;
        for (int i = 0; i < windows.Count; i++)
        {
            Rect overlay = windows[i].windowRect;
            if (overlay.yMin < 32f && overlay.yMax <= UI.screenHeight / 2f + 16f &&
                overlay.xMax > x && overlay.xMin < x + width)
            {
                y = Mathf.Max(y, overlay.yMax + 8f);
                overlayLeft = Mathf.Min(overlayLeft, overlay.xMin);
            }
        }
        if (y > UI.screenHeight / 3f && overlayLeft >= width + 20f)
        {
            x = overlayLeft - width - 8f;
            y = top;
        }
        var toggle = new Rect(x, y, width, 28f);
        rosterBounds = toggle;
        if (Widgets.ButtonText(toggle, "FPV_GroupRoster".Translate(visibleFlights.Count))) expanded = !expanded;
        if (!expanded) return;
        float height = Mathf.Min(visibleFlights.Count * 26f, Mathf.Clamp(UI.screenHeight - y - 200f, 48f, 234f));
        var panel = new Rect(x, y + 29f, width, height + 42f);
        rosterBounds = new Rect(x, y, width, panel.yMax - y);
        Widgets.DrawWindowBackground(panel);
        for (int i = 0; i < GroupCount; i++)
        {
            var button = new Rect(x + 5f + i * 52f, panel.y + 5f, 50f, 26f);
            if (Widgets.ButtonText(button, (i + 1).ToString()))
            {
                if (ev.control) Assign(i); else Recall(i);
            }
            TooltipHandler.TipRegion(button, "FPV_GroupShortcut".Translate(i + 1, Keys[i].ToString()));
        }

        var frame = new Rect(x + 4f, panel.y + 36f, width - 8f, height);
        var content = new Rect(0f, 0f, frame.width - 18f, visibleFlights.Count * 26f);
        Widgets.BeginScrollView(frame, ref scroll, content);
        for (int i = 0; i < visibleFlights.Count; i++)
        {
            var drone = visibleFlights[i];
            var row = new Rect(0f, i * 26f, content.width, 25f);
            if (Find.Selector.IsSelected(drone)) Widgets.DrawHighlightSelected(row);
            Widgets.DrawHighlightIfMouseover(row);
            GUI.DrawTexture(new Rect(2f, row.y + 2f, 21f, 21f), drone.ControlIcon);
            string label = drone.Operator?.LabelShortCap.ToString() ?? drone.LabelShortCap.ToString();
            Widgets.Label(new Rect(27f, row.y, row.width - 28f, row.height), label + "  " + drone.Charge01.ToStringPercent());
            if (Widgets.ButtonInvisible(row)) SelectFromBadge(drone);
        }
        Widgets.EndScrollView();
        if (Mouse.IsOver(panel) && ev.type == EventType.MouseDown) ev.Use();
    }

    internal static bool IsPointerInRoster(Map map, Vector2 pointer) =>
        map != null && map.GetComponent<DroneGroups>().rosterBounds.Contains(pointer);

    [HarmonyPatch(typeof(Selector), "SelectInsideDragBox")]
    private static class DroneBox
    {
        private static bool Prefix(Selector __instance)
        {
            if (Event.current?.alt != true || Find.CurrentMap == null) return true;
            var comp = Find.CurrentMap.GetComponent<DroneGroups>();
            var drones = ThingSelectionUtility.MultiSelectableThingsInScreenRectDistinct(__instance.dragBox.ScreenRect)
                .OfType<FlyingDrone>().Where(comp.Eligible).ToList();
            if (!Event.current.shift) __instance.ClearSelection();
            foreach (var drone in drones) __instance.Select(drone, false);
            return false;
        }
    }
}
