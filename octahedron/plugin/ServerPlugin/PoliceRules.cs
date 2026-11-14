namespace ServerPlugin;

// Whom the police go after when a ship fires near a station (SE-1490). Police.cs looks up the
// facts on the game's thread; this decides, with no game types, so tests/police-rules can run it.
internal static class PoliceRules
{
    public enum Side
    {
        // A player, an OCT NPC, or anyone else not at war with OCT
        Other,

        // An NPC of a faction OCT is at war with: pirates, hostile encounters
        Enemy,
    }

    public enum Pick
    {
        None,
        Attacker,
        Victim,
    }

    // An enemy NPC that shoots is a target, whatever it hits. Fire at a ship the police already
    // chase is self-defense or help: no target. Fire at an enemy NPC brings the police to fight
    // that NPC, not the shooter. Fire at anything else, players' and OCT's ships first of all,
    // makes the shooter the target.
    public static Pick Decide(Side attacker, Side victim, bool victimIsTarget) =>
        attacker == Side.Enemy ? Pick.Attacker
        : victimIsTarget ? Pick.None
        : victim == Side.Enemy ? Pick.Victim
        : Pick.Attacker;
}
