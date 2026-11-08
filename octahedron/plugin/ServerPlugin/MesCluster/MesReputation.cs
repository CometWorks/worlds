using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Game.World;

namespace ServerPlugin;

/// <summary>
/// Keeps MES damage triggers from taking reputation from the wrong players (SE-1294).
///
/// THIE's THIE-Bullet-Reputation-Loss-TriggerA counts any damage, collisions included, has no cooldown
/// and charges every member of the attacker's faction. Every Octahedron player is in OCT, so one
/// player bumping a THIE ship cost all of them 5 reputation per damage event.
///
/// A damage trigger with an action that changes the attacker's reputation ignores collision
/// (Deformation) and Environment damage here, and fires at most once per MesReputationCooldownMs.
/// The trigger is fixed when its ship first takes damage, so ships loaded from a save get it too.
/// A reputation change MES would spread over the attacker's faction stays with the attacker when that
/// faction is OCT: OCT is every player's default faction, not a team. Other factions keep MES's rule.
///
/// MES is compiled at session load, so its types are looked up by name. If one is missing, MES runs
/// unpatched and a Critical line says so.
/// </summary>
internal static class MesReputation
{
    private const string TriggerSystemType =
        "ModularEncountersSystems.Behavior.Subsystems.Trigger.TriggerSystem";
    private const string TriggerProfileType =
        "ModularEncountersSystems.Behavior.Subsystems.Trigger.TriggerProfile";
    private const string ActionProfileType =
        "ModularEncountersSystems.Behavior.Subsystems.Trigger.ActionProfile";
    private const string FactionHelperType = "ModularEncountersSystems.Helpers.FactionHelper";

    private static readonly string[] Ignored = ["Deformation", "Environment"];

    private static readonly string[] SpreadParameters =
    [
        "remoteControl",
        "amounts",
        "players",
        "factionTags",
        "applyReputationChangeToFactionMembers",
        "minRep",
        "maxRep",
    ];

    private static FieldInfo damageTriggers,
        excludedDamageTypes,
        actions,
        minCooldown,
        maxCooldown,
        cooldownTime,
        changeAttackerReputation;
    private static PropertyInfo actionReference;

    // Trigger profile -> Yes when one of its actions changes the attacker's reputation
    private static readonly ConditionalWeakTable<object, object> Checked = new();
    private static readonly object Yes = new(),
        No = new();

    private static bool Active => Plugin.Config is { Enabled: true, MesReputationFix: true };

    public static void Install(Harmony harmony, Assembly mes)
    {
        var system = mes.GetType(TriggerSystemType);
        var trigger = mes.GetType(TriggerProfileType);
        var watchers = system?.GetMethod("ProcessDamageTriggerWatchers");
        var spread = mes.GetType(FactionHelperType)
            ?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == "ChangePlayerReputationWithFactions"
                && m.GetParameters().Select(p => p.Name).SequenceEqual(SpreadParameters)
            );

        damageTriggers = system?.GetField("DamageTriggers");
        excludedDamageTypes = trigger?.GetField("ExcludedDamageTypes");
        actions = trigger?.GetField("Actions");
        minCooldown = trigger?.GetField("MinCooldownMs");
        maxCooldown = trigger?.GetField("MaxCooldownMs");
        cooldownTime = trigger?.GetField("CooldownTime");
        actionReference = mes.GetType(ActionProfileType)?.GetProperty("ActionReference");
        changeAttackerReputation = actionReference?.PropertyType.GetField(
            "ChangeAttackerReputation"
        );

        if (
            watchers == null
            || spread == null
            || damageTriggers == null
            || excludedDamageTypes?.FieldType != typeof(List<string>)
            || actions == null
            || minCooldown?.FieldType != typeof(float)
            || maxCooldown?.FieldType != typeof(float)
            || cooldownTime?.FieldType != typeof(int)
            || changeAttackerReputation?.FieldType != typeof(bool)
        )
        {
            Plugin.Log.Critical(
                "MES no longer has the trigger and reputation members this plugin patches. MES runs UNPATCHED: "
                    + "collisions cost reputation and attacks charge every OCT member"
            );
            return;
        }

        harmony.Patch(
            watchers,
            prefix: new HarmonyMethod(typeof(MesReputation), nameof(FixTriggers))
        );
        harmony.Patch(
            spread,
            prefix: new HarmonyMethod(typeof(MesReputation), nameof(AttackerOnly))
        );
        Plugin.Log.Info(
            "MES reputation patched: attacker reputation triggers ignore collisions and have a cooldown, OCT members are charged one by one"
        );
    }

    // Before MES matches a damage event against the damage triggers of a ship
    // ReSharper disable once InconsistentNaming
    private static void FixTriggers(object __instance)
    {
        if (!Active || damageTriggers.GetValue(__instance) is not IList triggers)
            return;

        float cooldown = Plugin.Config.MesReputationCooldownMs;
        foreach (var trigger in triggers)
        {
            if (trigger == null || Checked.GetValue(trigger, ChangesReputation) == No)
                continue;

            var excluded = (List<string>)excludedDamageTypes.GetValue(trigger);
            foreach (var type in Ignored)
                if (!excluded.Contains(type))
                    excluded.Add(type);

            if ((float)maxCooldown.GetValue(trigger) < cooldown)
            {
                minCooldown.SetValue(trigger, cooldown);
                maxCooldown.SetValue(trigger, cooldown);
                // MES draws the next cooldown when the trigger fires; one that has fired waits now
                if ((int)cooldownTime.GetValue(trigger) > 0)
                    cooldownTime.SetValue(trigger, (int)cooldown);
            }
        }
    }

    private static object ChangesReputation(object trigger) =>
        actions.GetValue(trigger) is IEnumerable list
        && list.Cast<object>()
            .Any(a =>
                a != null
                && actionReference.GetValue(a) is { } reference
                && (bool)changeAttackerReputation.GetValue(reference)
            )
            ? Yes
            : No;

    // MES adds every member of each listed player's faction when the flag is set; not for OCT
    private static void AttackerOnly(
        ref List<long> players,
        ref bool applyReputationChangeToFactionMembers
    )
    {
        if (!applyReputationChangeToFactionMembers || !Active || players == null)
            return;

        var oct = OctFaction.Faction;
        var factions = MySession.Static.Factions;
        if (oct == null || !players.Any(p => factions.GetPlayerFaction(p) == oct))
            return;

        var all = new List<long>(players);
        foreach (var player in players)
        {
            var faction = factions.GetPlayerFaction(player);
            if (faction == null || faction == oct)
                continue;
            foreach (var member in faction.Members.Keys)
                if (member != 0 && !all.Contains(member))
                    all.Add(member);
        }

        players = all;
        applyReputationChangeToFactionMembers = false;
    }
}
