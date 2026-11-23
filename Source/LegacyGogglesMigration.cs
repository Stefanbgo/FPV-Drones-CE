using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FPVDrones;

[HarmonyPatch(typeof(ScribeLoader), nameof(ScribeLoader.InitLoading))]
internal static class LegacyGogglesMigration
{
    private const string Goggles = "FPV_Goggles";
    private const string GogglesRecipe = "FPV_MakeGoggles";
    private const string GogglesReference = "Thing_" + Goggles;
    private const string BillReference = "Bill_" + GogglesRecipe + "_";

    private static void Postfix(ScribeLoader __instance)
    {
        // Old versions used the same package ID and game build. Clean the in-memory save before Scribe resolves defs and references.
        XmlNode game = __instance.curXmlParent?.SelectSingleNode("self::savegame/game");
        if (game == null) return;

        XmlNode[] fields = game.SelectNodes($".//*[text()='{Goggles}' or text()='{GogglesRecipe}' or " +
            $"starts-with(text(),'{GogglesReference}') or starts-with(text(),'{BillReference}')]")
            .Cast<XmlNode>().ToArray();
        var removedJobs = new HashSet<string>();
        foreach (XmlNode field in fields)
        {
            XmlNode owner = field.ParentNode;
            if (owner?.ParentNode == null) continue;

            string value = field.InnerText;
            if ((field.Name == "def" && value == Goggles && (owner["id"] != null || owner.ParentNode.Name == "slots")) ||
                (field.Name == "ThingDef" && value == Goggles && owner.ParentNode.Name == "HoldRecords") ||
                (field.Name == "recipe" && value == GogglesRecipe && owner.ParentNode.Name == "bills"))
            {
                owner.ParentNode.RemoveChild(owner);
            }
            else if (field.Name == "li" && owner.Name == "allowedDefs" && value == Goggles)
            {
                owner.RemoveChild(field);
            }
            else if (IsReferenceToRetiredObject(value))
            {
                RemoveJobOrClearReference(field, removedJobs);
            }
        }

        if (removedJobs.Count == 0) return;
        foreach (XmlNode field in game.SelectNodes(".//*[starts-with(text(),'Job_')]").Cast<XmlNode>().ToArray())
            if (removedJobs.Contains(field.InnerText)) ClearReference(field);
    }

    private static bool IsReferenceToRetiredObject(string value) =>
        HasNumericSuffix(value, GogglesReference) || HasNumericSuffix(value, BillReference);

    private static bool HasNumericSuffix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length &&
        int.TryParse(value.AsSpan(prefix.Length), out _);

    private static void RemoveJobOrClearReference(XmlNode field, HashSet<string> removedJobs)
    {
        XmlNode job = field.ParentNode;
        if (job.Name is "targetQueueA" or "targetQueueB") job = job.ParentNode;
        XmlNode container = job.ParentNode;
        if (container?.Name == "jobs" && job.Name == "curJob")
        {
            if (container["curDriver"] is XmlNode driver) container.RemoveChild(driver);
            container.RemoveChild(job);
        }
        else if (job.Name == "job" && container?.ParentNode?.Name == "jobs")
        {
            container.ParentNode.RemoveChild(container);
        }
        else
        {
            ClearReference(field);
            return;
        }

        if (job["loadID"] is XmlNode id) removedJobs.Add("Job_" + id.InnerText);
    }

    private static void ClearReference(XmlNode field)
    {
        XmlNode owner = field.ParentNode;
        if (owner?.ParentNode?.Name == "reservations") owner.ParentNode.RemoveChild(owner);
        else if (field.Name == "li" && owner?.Name is "itemsNotForSale" or "unpackedCaravanItems") owner.RemoveChild(field);
        else if (field.Name is not ("label" or "name" or "first" or "last" or "nick")) field.InnerText = "null";
    }

    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.WasDefRemoved))]
    private static class RecognizeRetiredDefs
    {
        private static void Postfix(string defName, Type type, ref bool __result)
        {
            if ((type == typeof(ThingDef) && defName == Goggles) ||
                (type == typeof(RecipeDef) && defName == GogglesRecipe))
                __result = true;
        }
    }
}
