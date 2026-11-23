using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace FPVDrones;

public sealed class FloatMenuOptionProvider_DroneKit : FloatMenuOptionProvider
{
    protected override bool Drafted => true;
    protected override bool Undrafted => true;
    protected override bool Multiselect => false;

    public override IEnumerable<FloatMenuOption> GetOptionsFor(Thing clickedThing, FloatMenuContext context)
    {
        if (!DroneControl.IsDroneItem(clickedThing) && clickedThing.def.defName != "FPV_Controller")
            yield break;
        var pawn = context.FirstSelectedPawn;
        if (!pawn.IsColonistPlayerControlled || !clickedThing.Spawned) yield break;
        string label = "FPV_UI_PickUp".Translate(clickedThing.LabelNoCount);
        if (!pawn.CanReserveAndReach(clickedThing, PathEndMode.ClosestTouch, Danger.Deadly))
        {
            yield return new FloatMenuOption(label + " (" + "FPV_UI_UnreachableItem".Translate() + ")", null);
            yield break;
        }
        if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, clickedThing, 1))
        {
            yield return new FloatMenuOption(label + " (" + "FPV_UI_HeavyItem".Translate() + ")", null);
            yield break;
        }
        yield return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(label, () =>
        {
            clickedThing.SetForbidden(false, false);
            var job = JobMaker.MakeJob(JobDefOf.TakeInventory, clickedThing);
            job.count = 1;
            job.checkEncumbrance = true;
            pawn.jobs.TryTakeOrderedJob(job);
        }), pawn, clickedThing);
    }
}
