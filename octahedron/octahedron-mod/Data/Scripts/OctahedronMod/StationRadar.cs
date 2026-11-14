using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;
using IngameCubeBlock = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IngameTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace OctahedronMod
{
    // The radar on the stations' big map LCD (SE-1380): the grids within Range of the screen, one
    // marker per logical group (connectors, rotors, pistons), above or below the station's deck
    // plane on a stalk. The contact count is in the top right corner, and the nearest 30 are
    // listed by distance in a column on each side, left first (SE-1502). A contact is green when
    // it is the viewer's or a friend's, red when hostile and white otherwise, in one of several
    // shades, shared by its marker and its row, that it keeps for as long as it stays listed. The station's
    // own installations are no contacts: a group with a static grid that the screen's owner or
    // nobody owns, like the shipyard and the lane computers. The lane computers show as the gates
    // instead, listed at the bottom by where they lead. Players' and NPC ships, and players'
    // bases, show.
    [MyTextSurfaceScript("OctahedronRadar", "Octahedron Radar")]
    public class StationRadar : MyTSSCommon
    {
        // 2 km since 2026-11-13; Arthur's Lcd Mod radar the screen ran before was set to 6 km
        const double Range = 2000;
        static readonly double[] Rings = { 500, 1000, 2000 };
        const int MaxMarkers = 40;
        const int Rows = 15;

        // How flat the deck plane looks, and how far up a contact's height goes, per radius
        const float Tilt = 0.45f;
        const float Height = 0.8f;

        // The shades of each relation's color: friendly green, neutral white and hostile red.
        // Each listed contact takes the least used shade of its color, the first ones differing
        // the most.
        const int Shades = 10;
        const int Friendly = 0;
        const int Neutral = 1;
        const int Hostile = 2;
        static readonly float[] Hues = { 0.33f, 0f, 0f };
        static readonly float[] Saturations = { 0.75f, 0f, 0.8f };
        static readonly float[] HueShifts =
        {
            0,
            0.05f,
            -0.05f,
            0.025f,
            -0.025f,
            0.05f,
            -0.05f,
            0.025f,
            -0.025f,
            0,
        };
        static readonly float[] Values = { 1, 0.8f, 0.8f, 0.65f, 0.65f, 1, 1, 0.9f, 0.9f, 0.55f };
        static readonly Color[] Palette = new Color[3 * Shades];

        // The gates in the cyan blue of the gate models
        static readonly Color Gate = new Color(60, 190, 255);

        static StationRadar()
        {
            for (var relation = 0; relation < 3; relation++)
            for (var i = 0; i < Shades; i++)
            {
                var hue = Hues[relation] + (relation == Neutral ? 0 : HueShifts[i]);
                // White has no hue to shift: only its brightness differs
                var value = relation == Neutral ? 0.55f + 0.05f * i : Values[i];
                Palette[relation * Shades + i] = new Vector3(
                    (hue + 1) % 1,
                    Saturations[relation],
                    value
                ).HSVtoColor();
            }
            // The brightest white first
            Array.Reverse(Palette, Neutral * Shades, Shades);
        }

        readonly List<IMyCubeGrid> group = new List<IMyCubeGrid>();
        readonly HashSet<long> seen = new HashSet<long>();
        readonly List<Contact> contacts = new List<Contact>();
        readonly List<Contact> gates = new List<Contact>();

        // The palette index of each listed contact, by its largest grid's id, and how many listed
        // contacts use each
        readonly Dictionary<long, int> colors = new Dictionary<long, int>();
        readonly List<long> unlisted = new List<long>();
        readonly int[] used = new int[3 * Shades];
        readonly StringBuilder measured = new StringBuilder();
        readonly Dictionary<string, string> clipped = new Dictionary<string, string>();

        struct Contact
        {
            public long Id;
            public string Name;
            public Vector3D Local;
            public double Distance;
            public int Relation;
        }

        public StationRadar(IngameTextSurface surface, IngameCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        public override void Run()
        {
            base.Run();
            var block = m_block as IMyCubeBlock;
            if (block?.CubeGrid == null)
                return;
            Collect(block);
            using (var frame = m_surface.DrawFrame())
                Draw(frame);
        }

        void Collect(IMyCubeBlock block)
        {
            contacts.Clear();
            gates.Clear();
            seen.Clear();

            // The deck plane is the station grid's; up on the map is away from the viewer
            var center = block.WorldMatrix.Translation;
            var up = block.CubeGrid.WorldMatrix.Up;
            var away = Vector3D.Normalize(Vector3D.Reject(block.WorldMatrix.Backward, up));
            var right = Vector3D.Cross(away, up);

            var sphere = new BoundingSphereD(center, Range);
            var player = MyAPIGateway.Session?.Player;
            foreach (var entity in MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere))
            {
                var grid = entity as IMyCubeGrid;
                if (grid?.Physics == null || grid.MarkedForClose || seen.Contains(grid.EntityId))
                    continue;

                group.Clear();
                MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Logical, group);
                if (group.Count == 0)
                    group.Add(grid);
                IMyCubeGrid largest = null;
                var volume = -1.0;
                var skip = false;
                IMyCubeGrid gate = null;
                foreach (var g in group)
                {
                    seen.Add(g.EntityId);
                    var installation = g == block.CubeGrid || Installation(g, block.OwnerId);
                    skip |= installation;
                    if (installation && Destination(g) != null)
                        gate = g;
                    var v = g.WorldAABB.Volume;
                    if (v > volume)
                    {
                        volume = v;
                        largest = g;
                    }
                }
                if (skip && gate == null)
                    continue;

                var shown = gate ?? largest;
                var offset = shown.WorldAABB.Center - center;
                var distance = offset.Length();
                if (distance > Range)
                    continue;

                (gate != null ? gates : contacts).Add(
                    new Contact
                    {
                        Id = shown.EntityId,
                        Name = gate != null ? Destination(gate) : largest.DisplayName ?? "",
                        Local = new Vector3D(offset.Dot(right), offset.Dot(away), offset.Dot(up)),
                        Distance = distance,
                        Relation = gate != null ? Neutral : Relation(largest, player),
                    }
                );
            }

            contacts.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            gates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            KeepColors();
        }

        static int Relation(IMyCubeGrid grid, IMyPlayer player)
        {
            var owners = grid.BigOwners;
            if (owners.Count == 0 || player == null)
                return Neutral;
            switch (player.GetRelationTo(owners[0]))
            {
                case MyRelationsBetweenPlayerAndBlock.Enemies:
                    return Hostile;
                case MyRelationsBetweenPlayerAndBlock.Owner:
                case MyRelationsBetweenPlayerAndBlock.FactionShare:
                case MyRelationsBetweenPlayerAndBlock.Friends:
                    return Friendly;
                default:
                    return Neutral;
            }
        }

        // The listed contacts keep their shades; one that drops off the list or changes sides
        // frees its shade, and one new on the list takes the least used shade of its color
        void KeepColors()
        {
            var listed = Math.Min(contacts.Count, 2 * Rows);
            unlisted.Clear();
            foreach (var pair in colors)
            {
                var found = false;
                for (var i = 0; i < listed && !found; i++)
                    found =
                        contacts[i].Id == pair.Key && contacts[i].Relation == pair.Value / Shades;
                if (!found)
                    unlisted.Add(pair.Key);
            }
            foreach (var id in unlisted)
            {
                used[colors[id]]--;
                colors.Remove(id);
            }
            for (var i = 0; i < listed; i++)
            {
                var c = contacts[i];
                if (colors.ContainsKey(c.Id))
                    continue;
                var best = c.Relation * Shades;
                for (var shade = best + 1; shade < (c.Relation + 1) * Shades; shade++)
                    if (used[shade] < used[best])
                        best = shade;
                used[best]++;
                colors[c.Id] = best;
            }
        }

        // Its shade while listed; one past the list is drawn dim in its relation's color
        Color Paint(Contact c)
        {
            int index;
            return colors.TryGetValue(c.Id, out index)
                ? Palette[index]
                : Palette[c.Relation * Shades] * 0.5f;
        }

        // Where a lane computer's gate leads: its grid is named "TL 07 North Station > Beta
        // Station". A lane to a planet's pole leads to Earth, which has no station yet.
        static string Destination(IMyCubeGrid grid)
        {
            var name = grid.DisplayName ?? "";
            var arrow = name.LastIndexOf(" > ", StringComparison.Ordinal);
            if (!name.StartsWith("TL ", StringComparison.Ordinal) || arrow < 0)
                return null;
            var to = name.Substring(arrow + 3);
            return to.EndsWith(" Station", StringComparison.Ordinal) ? to : "Earth";
        }

        // A station installation: a static grid of the station's owner or of nobody
        static bool Installation(IMyCubeGrid grid, long stationOwner)
        {
            if (!grid.IsStatic)
                return false;
            var owners = grid.BigOwners;
            return owners.Count == 0 || owners.Contains(stationOwner);
        }

        void Draw(MySpriteDrawFrame frame)
        {
            var area = m_surface.SurfaceSize;
            var origin = (m_surface.TextureSize - area) / 2f;
            var scale = Math.Min(area.X, area.Y) / 512f;
            var dim = m_foregroundColor * 0.35f;

            frame.Add(
                new MySprite(
                    SpriteType.TEXTURE,
                    "SquareSimple",
                    m_surface.TextureSize / 2f,
                    m_surface.TextureSize,
                    m_backgroundColor
                )
            );

            // The deck plane's ellipse in the middle, with room for heights; a list column each side
            // and the gates below
            var radius = Math.Min(area.X * 0.14f, area.Y * 0.36f);
            var middle = origin + new Vector2(area.X / 2f, area.Y * 0.42f);

            // Range rings at 500 m, 1 km and 2 km
            foreach (var ring in Rings)
            {
                var r = (float)(radius * ring / Range);
                frame.Add(
                    new MySprite(
                        SpriteType.TEXTURE,
                        "CircleHollow",
                        middle,
                        new Vector2(2 * r, 2 * r * Tilt),
                        dim
                    )
                );
                Text(
                    frame,
                    ring < 1000 ? $"{ring:0} m" : $"{ring / 1000:0} km",
                    middle + new Vector2(r - 30 * scale, 2 * scale),
                    dim,
                    0.6f * scale
                );
            }
            AddLine(
                frame,
                middle - new Vector2(radius, 0),
                middle + new Vector2(radius, 0),
                dim,
                scale
            );
            AddLine(
                frame,
                middle - new Vector2(0, radius * Tilt),
                middle + new Vector2(0, radius * Tilt),
                dim,
                scale
            );
            frame.Add(
                new MySprite(
                    SpriteType.TEXTURE,
                    "Triangle",
                    middle,
                    new Vector2(20, 20) * scale,
                    m_foregroundColor
                )
            );
            Text(
                frame,
                "RADAR",
                origin + new Vector2(16, 10) * scale,
                m_foregroundColor,
                1.2f * scale
            );
            Text(
                frame,
                $"Contacts: {contacts.Count}",
                origin + new Vector2(area.X - 16 * scale, 14 * scale),
                m_foregroundColor,
                0.9f * scale,
                TextAlignment.RIGHT
            );

            // Farthest first, so the near ones draw on top
            var k = (float)(radius / Range);
            foreach (var g in gates)
                Marker(frame, g, middle, k, scale, "CircleHollow", Gate);
            for (var i = Math.Min(contacts.Count, MaxMarkers) - 1; i >= 0; i--)
            {
                var c = contacts[i];
                Marker(
                    frame,
                    c,
                    middle,
                    k,
                    scale,
                    c.Local.Z >= 0 ? "Triangle" : "SquareSimple",
                    Paint(c)
                );
            }

            // Nearest first: down the left column, then down the right one; the rest is left out
            var top = 60 * scale;
            var row = (area.Y - top - 8 * scale) / Rows;
            var size = Math.Min(0.7f * scale, row / 37f);
            var width = middle.X - origin.X - radius - 32 * scale;
            for (var i = 0; i < contacts.Count && i < 2 * Rows; i++)
            {
                var c = contacts[i];
                var x = i < Rows ? 16 * scale : area.X - 16 * scale - width;
                Row(
                    frame,
                    c,
                    origin + new Vector2(x, top + (i % Rows) * row),
                    width,
                    size,
                    Paint(c)
                );
            }

            // The gates below the map, as many as fit
            var gateTop = area.Y * 0.68f;
            var gateRows = (int)((area.Y - gateTop - 8 * scale) / row);
            for (var i = 0; i < gates.Count && i < gateRows; i++)
                Row(
                    frame,
                    gates[i],
                    origin + new Vector2(area.X / 2f - radius, gateTop + i * row),
                    2 * radius,
                    size,
                    Gate
                );
        }

        void Marker(
            MySpriteDrawFrame frame,
            Contact c,
            Vector2 middle,
            float k,
            float scale,
            string shape,
            Color color
        )
        {
            var foot = middle + new Vector2((float)c.Local.X * k, -(float)c.Local.Y * k * Tilt);
            var spot = foot - new Vector2(0, (float)c.Local.Z * k * Height);
            AddLine(frame, foot, spot, color * 0.5f, scale);
            frame.Add(
                new MySprite(
                    SpriteType.TEXTURE,
                    "Circle",
                    foot,
                    new Vector2(6, 6) * scale,
                    color * 0.5f
                )
            );
            frame.Add(
                new MySprite(SpriteType.TEXTURE, shape, spot, new Vector2(16, 16) * scale, color)
            );
        }

        // A list row: the distance, and the name cut to fit the width
        void Row(
            MySpriteDrawFrame frame,
            Contact c,
            Vector2 at,
            float width,
            float size,
            Color color
        )
        {
            var distance = c.Distance < 1000 ? $"{c.Distance:0} m" : $"{c.Distance / 1000:0.00} km";
            Text(frame, distance, at, color, size);
            var indent = Measure("0.00 km ", size);
            Text(
                frame,
                Clip(c.Name, width - indent, size),
                at + new Vector2(indent, 0),
                color,
                size
            );
        }

        float Measure(string text, float size)
        {
            measured.Clear().Append(text);
            return m_surface.MeasureStringInPixels(measured, "White", size).X;
        }

        // The name cut to fit the column; the column's width stays, so each name is cut once
        string Clip(string name, float width, float size)
        {
            string fit;
            if (clipped.TryGetValue(name, out fit))
                return fit;
            fit = name;
            for (var n = name.Length; n > 0 && Measure(fit, size) > width; n--)
                fit = name.Substring(0, n - 1) + "…";
            if (clipped.Count > 1000)
                clipped.Clear();
            clipped[name] = fit;
            return fit;
        }

        static void Text(
            MySpriteDrawFrame frame,
            string text,
            Vector2 at,
            Color color,
            float size,
            TextAlignment alignment = TextAlignment.LEFT
        )
        {
            var sprite = MySprite.CreateText(text, "White", color, size, alignment);
            sprite.Position = at;
            frame.Add(sprite);
        }
    }
}
