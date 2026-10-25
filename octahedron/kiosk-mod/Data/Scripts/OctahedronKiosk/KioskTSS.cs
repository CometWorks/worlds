using System;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace OctahedronKiosk
{
    /// <summary>
    /// The kiosk's screen script. The Octahedron server plugin selects it on every station
    /// kiosk; it runs the touch app once TouchScreenAPI is ready.
    /// </summary>
    [MyTextSurfaceScript(KioskSession.Script, "Octahedron Kiosk")]
    public class KioskTSS : MyTSSCommon
    {
        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        private readonly IMyCubeBlock block;
        private readonly IMyTextSurface surface;
        private KioskApp app;
        private bool failed;

        public KioskTSS(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size)
        {
            this.block = block;
            this.surface = surface;
            surface.ScriptBackgroundColor = Color.Black;
        }

        public override void Dispose()
        {
            base.Dispose();
            app?.Dispose();
            app = null;
        }

        public override void Run()
        {
            base.Run();
            if (failed || MyAPIGateway.Utilities.IsDedicated)
                return;
            try
            {
                var session = KioskSession.Instance;
                if (app == null)
                {
                    if (session?.Api == null || !session.Api.IsReady)
                    {
                        using (var frame = m_surface.DrawFrame())
                            frame.Add(Message("Octahedron Kiosk\nWaiting for TouchScreenAPI"));
                        return;
                    }
                    app = new KioskApp(block, surface);
                    app.Theme.Scale = Math.Min(
                        Math.Max(
                            Math.Min(surface.SurfaceSize.X, surface.SurfaceSize.Y) / 512,
                            0.5f
                        ),
                        2
                    );
                    app.Cursor.Scale = app.Theme.Scale;
                }
                app.Tick();
                using (var frame = m_surface.DrawFrame())
                {
                    app.ForceUpdate();
                    frame.AddRange(app.GetSprites());
                }
            }
            catch (Exception e)
            {
                failed = true;
                app?.Dispose();
                app = null;
                MyLog.Default.WriteLineAndConsole($"Octahedron kiosk screen failed: {e}");
            }
        }

        private MySprite Message(string text) =>
            new MySprite
            {
                Type = SpriteType.TEXT,
                Data = text,
                RotationOrScale = 0.8f,
                Color = Color.White,
                Alignment = TextAlignment.CENTER,
                Position =
                    (surface.TextureSize - surface.SurfaceSize) / 2f
                    + new Vector2(surface.SurfaceSize.X / 2, surface.SurfaceSize.Y / 3),
            };
    }
}
