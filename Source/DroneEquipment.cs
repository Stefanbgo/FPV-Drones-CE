using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FPVDrones;

public sealed class FpvMod : Mod
{
    public FpvMod(ModContentPack content) : base(content)
    {
        // A Core-only CE load can otherwise leave this recipe off the machining table.
        CombatExtended.Compatibility.Patches.UsedAmmoCallbacks.Add(UsedAmmunition);
    }

    private static IEnumerable<ThingDef> UsedAmmunition()
    {
        if (DefDatabase<ThingDef>.GetNamedSilentFail("Ammo_RPG7Grenade_Frag") is CombatExtended.AmmoDef fragmentationRound)
            yield return fragmentationRound;
    }
}

public sealed class CompProperties_DroneBattery : CompProperties
{
    public CompProperties_DroneBattery()
    {
        compClass = typeof(CompDroneBattery);
    }
}

public sealed class CompDroneBattery : ThingComp
{
    // 14,400 units represent both 4,800 flight ticks and 3,600 charging ticks exactly.
    private const int FullChargeUnits = 14400;
    private const int ChargeUnitsPerTick = 4;

    private int chargeUnits = FullChargeUnits;

    public float Charge01
    {
        get => chargeUnits / (float)FullChargeUnits;
        set => chargeUnits = (int)Math.Round(Math.Clamp(value, 0f, 1f) * FullChargeUnits);
    }

    internal bool IsFull => chargeUnits >= FullChargeUnits;

    internal void ChargeOneTick()
    {
        chargeUnits = Math.Min(FullChargeUnits, chargeUnits + ChargeUnitsPerTick);
    }

    public override string CompInspectStringExtra()
    {
        return "FPV_BatteryCharge".Translate(Charge01.ToString("P0"));
    }

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_Values.Look(ref chargeUnits, "chargeUnits", FullChargeUnits);
    }
}

public sealed class Building_DroneCharger : Building_Storage
{
    private const float ChargingWatts = 100f;

    private CompPowerTrader Power => GetComp<CompPowerTrader>();

    private static bool HasPower(CompPowerTrader power) => power?.PowerOn == true && power.PowerNet != null;

    private CompDroneBattery DockedBattery => GetSlotGroup()?.HeldThings
        .FirstOrDefault(thing => thing.def.defName == "FPV_Scout")?.TryGetComp<CompDroneBattery>();

    protected override void Tick()
    {
        base.Tick();

        CompPowerTrader power = Power;
        CompDroneBattery battery = DockedBattery;
        bool needsCharge = battery != null && !battery.IsFull;
        if (power == null)
            return;

        power.PowerOutput = needsCharge ? -ChargingWatts : 0f;
        if (needsCharge && HasPower(power))
            battery.ChargeOneTick();
    }

    public override string GetInspectString()
    {
        string storageInspection = base.GetInspectString();
        CompDroneBattery battery = DockedBattery;
        if (battery == null)
            return storageInspection;

        string status = battery.IsFull ? "FPV_ChargerReady".Translate() :
            HasPower(Power) ? "FPV_ChargerCharging".Translate() : "FPV_ChargerWaiting".Translate();
        return string.Join("\n", storageInspection, battery.CompInspectStringExtra(), status).Trim();
    }
}

public sealed class RecipeWorker_DroneAmmoMode : RecipeWorker
{
    internal bool MatchesCurrentMode
    {
        get
        {
            bool requiresAmmo = !recipe.defName.EndsWith("NoAmmo", StringComparison.Ordinal);
            return requiresAmmo == CombatExtended.Controller.settings.EnableAmmoSystem;
        }
    }

    public override bool AvailableOnNow(Thing thing, BodyPartRecord part = null)
    {
        return MatchesCurrentMode;
    }
}

// The bill list uses RecipeWorker.AvailableOnNow, but existing bills are checked separately.
[HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.ShouldDoNow))]
internal static class Patch_KamikazeBillAmmoMode
{
    private static void Postfix(Bill_Production __instance, ref bool __result)
    {
        if (__result && __instance.recipe.Worker is RecipeWorker_DroneAmmoMode worker)
            __result = worker.MatchesCurrentMode;
    }
}
