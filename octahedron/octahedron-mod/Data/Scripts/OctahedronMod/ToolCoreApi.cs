using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Entity;

namespace ToolCoreApi
{
    // The part of ToolCore's mod API (its API/TCApi.cs) the filaments use: a tool's event
    // states, like Hit while it works on a block
    public class TCApi
    {
        // ToolCore's Trigger values
        public const int Hit = 64;

        const long Channel = 2172757428;

        Action<MyEntity, Action<int, bool>> monitor;
        Action<MyEntity, Action<int, bool>> unmonitor;
        bool registered;

        public bool IsReady { get; private set; }

        // Called with (state, active) on each change of a tool's event states
        public void MonitorEvents(MyEntity tool, Action<int, bool> action) =>
            monitor?.Invoke(tool, action);

        public void UnMonitorEvents(MyEntity tool, Action<int, bool> action) =>
            unmonitor?.Invoke(tool, action);

        // ToolCore answers the request if it is loaded already, or sends its methods
        // when it loads
        public void Load()
        {
            registered = true;
            MyAPIGateway.Utilities.RegisterMessageHandler(Channel, Receive);
            MyAPIGateway.Utilities.SendModMessage(Channel, "ApiEndpointRequest");
        }

        public void Unload()
        {
            if (registered)
                MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, Receive);
            registered = false;
            monitor = unmonitor = null;
            IsReady = false;
        }

        void Receive(object message)
        {
            var methods = message as IReadOnlyDictionary<string, Delegate>;
            if (IsReady || methods == null)
                return;
            Delegate d;
            if (methods.TryGetValue("RegisterEventMonitor", out d))
                monitor = d as Action<MyEntity, Action<int, bool>>;
            if (methods.TryGetValue("UnRegisterEventMonitor", out d))
                unmonitor = d as Action<MyEntity, Action<int, bool>>;
            IsReady = monitor != null && unmonitor != null;
        }
    }
}
