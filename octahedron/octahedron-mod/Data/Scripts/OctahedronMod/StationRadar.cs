using System;
using System.Collections.Generic;
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
    // plane on a stalk, and the nearest of them listed below. It leaves out the station's own installations: a group with a static grid
    // that the screen's owner or nobody owns, like the shipyard and the lane computers. Players'
    // and NPC ships, and players' bases, show.
    [MyTextSurfaceScript("OctahedronRadar", "Octahedron Radar")]
    public class StationRadar : MyTSSCommon
    {
        // Arthur's Lcd Mod radar the screen ran before was set to 6 km
        const double Range = 6000;
        const int MaxContacts = 40;
        const int MaxListed = 4;

        // How flat the deck plane looks, and how far up a contact's height goes, per radius
        const float Tilt = 0.45f;
        const float Height = 0.8f;

        static readonly Color Hostile = new Color(255, 70, 60);
        static readonly Color Friendly = new Color(90, 220, 120);

        readonly List<IMyCubeGrid> group = new List<IMyCubeGrid>();
        readonly HashSet<long> seen = new HashSet<long>();
        readonly List<Contact> contacts = new List<Contact>();

        struct Contact
        {
            public string Name;
            public Vector3D Local;
            public double Distance;
            public Color Color;
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
                foreach (var g in group)
                {
                    seen.Add(g.EntityId);
                    skip |= g == block.CubeGrid || Installation(g, block.OwnerId);
                    var v = g.WorldAABB.Volume;
                    if (v > volume)
                    {
                        volume = v;
                        largest = g;
                    }
                }
                if (skip)
                    continue;

                var offset = largest.WorldAABB.Center - center;
                var distance = offset.Length();
                if (distance > Range)
                    continue;

                var owners = largest.BigOwners;
                var owner = owners.Count > 0 ? owners[0] : 0;
                var relation =
                    owner == 0 || player == null
                        ? MyRelationsBetweenPlayerAndBlock.NoOwnership
                        : player.GetRelationTo(owner);
                contacts.Add(
                    new Contact
                    {
                        Name = largest.DisplayName ?? "",
                        Local = new Vector3D(offset.Dot(right), offset.Dot(away), offset.Dot(up)),
                        Distance = distance,
                        Color = Paint(relation),
                    }
                );
            }

            contacts.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (contacts.Count > MaxContacts)
                contacts.RemoveRange(MaxContacts, contacts.Count - MaxContacts);
        }

        // A station installation: a static grid of the station's owner or of nobody
        static bool Installation(IMyCubeGrid grid, long stationOwner)
        {
            if (!grid.IsStatic)
                return false;
            var owners = grid.BigOwners;
            return owners.Count == 0 || owners.Contains(stationOwner);
        }

        Color Paint(MyRelationsBetweenPlayerAndBlock relation)
        {
            switch (relation)
            {
                case MyRelationsBetweenPlayerAndBlock.Enemies:
                    return Hostile;
                case MyRelationsBetweenPlayerAndBlock.Owner:
                case MyRelationsBetweenPlayerAndBlock.FactionShare:
                case MyRelationsBetweenPlayerAndBlock.Friends:
                    return Friendly;
                case MyRelationsBetweenPlayerAndBlock.NoOwnership:
                    return m_foregroundColor * 0.6f;
                default:
                    return m_foregroundColor;
            }
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

            // The deck plane's ellipse in the upper part, with room for heights; the list below
            var radius = Math.Min(area.X * 0.4f, area.Y * 0.24f / Tilt);
            var middle = origin + new Vector2(area.X / 2f, area.Y * 0.4f);

            for (var i = 1; i <= 3; i++)
            {
                var r = radius * i / 3f;
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
                    $"{Range / 3000 * i:0} km",
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

            // Farthest first, so the near ones draw on top
            var k = (float)(radius / Range);
            for (var i = contacts.Count - 1; i >= 0; i--)
            {
                var c = contacts[i];
                var foot = middle + new Vector2((float)c.Local.X * k, -(float)c.Local.Y * k * Tilt);
                var spot = foot - new Vector2(0, (float)c.Local.Z * k * Height);
                AddLine(frame, foot, spot, c.Color * 0.5f, scale);
                frame.Add(
                    new MySprite(
                        SpriteType.TEXTURE,
                        "Circle",
                        foot,
                        new Vector2(6, 6) * scale,
                        c.Color * 0.5f
                    )
                );
                frame.Add(
                    new MySprite(
                        SpriteType.TEXTURE,
                        c.Local.Z >= 0 ? "Triangle" : "SquareSimple",
                        spot,
                        new Vector2(16, 16) * scale,
                        c.Color
                    )
                );
            }

            var line = origin + new Vector2(16 * scale, area.Y * 0.66f);
            Text(
                frame,
                contacts.Count == 0 ? "No contacts" : $"Contacts: {contacts.Count}",
                line,
                m_foregroundColor,
                0.9f * scale
            );
            for (var i = 0; i < contacts.Count && i < MaxListed; i++)
            {
                var c = contacts[i];
                line += new Vector2(0, 30 * scale);
                Text(
                    frame,
                    $"{c.Distance / 1000:0.00} km  {Clip(c.Name)}",
                    line,
                    c.Color,
                    0.8f * scale
                );
            }
        }

        static string Clip(string name) => name.Length > 32 ? name.Substring(0, 31) + "…" : name;

        static void Text(MySpriteDrawFrame frame, string text, Vector2 at, Color color, float size)
        {
            var sprite = MySprite.CreateText(text, "White", color, size, TextAlignment.LEFT);
            sprite.Position = at;
            frame.Add(sprite);
        }
    }
}
