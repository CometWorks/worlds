using System;
using System.Globalization;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;

namespace ServerPlugin;

// Hangar's retrieval fee: a ship comes back free in the safe zone it was stored in, the same
// station, and for HangarRetrieveFeePer10Pcu SC per 10 PCU, paid to OCT, anywhere else. Hangar's
// own AdminSafeZonesOnly keeps storing and retrieving to the stations' zones, which are the
// world's only admin safe zones. Bound by reflection to Hangar's HangarHooks.BeforeLoad, so the
// plugin still runs without Hangar or with one older than 1.1.0.
internal static class HangarFee
{
    private static bool bound;
    private static bool missingOctLogged;

    // Once the session is up, when every plugin's assembly is loaded
    public static void Bind()
    {
        if (bound || MySession.Static?.Ready != true)
            return;
        bound = true;

        if (AccessTools.TypeByName("ServerPlugin.HangarStorage") == null)
        {
            Plugin.Log.Info("Hangar is not loaded, no retrieval fee");
            return;
        }
        var hook = AccessTools.TypeByName("ServerPlugin.HangarHooks")?.GetField("BeforeLoad");
        if (hook?.FieldType != typeof(Func<long, string, string, int, string>))
        {
            Plugin.Log.Error("Hangar is older than 1.1.0, ships come back from it for free");
            return;
        }
        hook.SetValue(null, (Func<long, string, string, int, string>)BeforeLoad);
        Plugin.Log.Info("Hangar's retrieval fee is on");
    }

    // Returns why the ship can't come back, or null; takes the fee when it can
    private static string BeforeLoad(long identityId, string savedZone, string zone, int pcu)
    {
        var config = Plugin.Config;
        if (!config.Enabled || savedZone == zone)
            return null;
        var fee = MoneyMath.RetrieveFee(pcu, config.HangarRetrieveFeePer10Pcu);
        if (fee == 0)
            return null;

        var oct = MySession.Static.Factions.TryGetFactionByTag(config.MoneyAuthorityFactionTag);
        if (oct == null)
        {
            if (!missingOctLogged)
                Plugin.Log.Error(
                    $"Hangar fee: no faction tagged {config.MoneyAuthorityFactionTag}, ships come back for free"
                );
            missingOctLogged = true;
            return null;
        }
        missingOctLogged = false;

        var from = savedZone == "" ? "outside the stations" : $"at {savedZone}";
        // Refused when the balance is short; on a cluster the World Authority decides
        if (!MyBankingSystem.ChangeBalance(identityId, -fee))
        {
            var balance = MyBankingSystem.Static.TryGetAccountInfo(identityId, out var account)
                ? account.Balance
                : 0;
            return $"Your ship was stored {from}. Bringing it here costs {Sc(fee)} "
                + $"({N(pcu)} PCU), you have {Sc(balance)}.";
        }
        if (!MyBankingSystem.ChangeBalance(oct.FactionId, fee))
            Plugin.Log.Error($"Hangar fee: {fee} SC taken from {identityId} but not paid to OCT");

        Plugin.Log.Info(
            $"Hangar fee: {identityId} paid {fee} SC for {pcu} PCU from '{savedZone}' to '{zone}'"
        );
        MyVisualScriptLogicProvider.SendChatMessage(
            $"You paid {Sc(fee)} to bring your ship here, it was stored {from}.",
            "Hangar",
            identityId
        );
        return null;
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Sc(long amount) => N(amount) + " SC";
}
