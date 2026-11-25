using Sculpt.Core;
using Sculpt.Game.Cameras;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Rendering;
using Sculpt.Game.UI;
using UnityEngine;

namespace Sculpt.Game.Diagnostics
{
    /// <summary>
    /// One call from the bootstrap that sets up everything for frame pacing and measuring: the render profile (V-Sync,
    /// frame cap, frame queue, lean rendering), the measurements, the overlay, and a benchmark run when the command
    /// line asks for one. Kept in one place so the bootstrap needs a single line.
    /// </summary>
    public static class PerfSetup
    {
        /// <summary>
        /// Unity calls every OnGUI twice a frame: once to lay out GUILayout controls, once to draw. The game HUD places
        /// everything by hand, so the layout call does nothing except run all the HUD code (and make its garbage) a
        /// second time. Switching it off for those scripts halves the HUD's cost. The settings menu does use GUILayout,
        /// so it keeps its layout pass, but only while it is open (see <see cref="PerfRuntime"/>).
        /// </summary>
        private static void SkipHudLayoutPass(GameObject host)
        {
            foreach (MonoBehaviour mb in host.GetComponents<MonoBehaviour>())
                if (mb is CombatHud || mb is SculptHud || mb is FeelHud || mb is DevHud) mb.useGUILayout = false;
        }

        public static void Install(GameObject host, NetSession session, LocalPlayer player, GameInput input, CameraRig rig, SubGrid grid)
        {
            RenderProfile profile = RenderProfile.Install(host, rig.Camera);
            PerfRuntime perf = PerfRuntime.Install(host, session, player, input, profile);
            PerfHud.Install(host, perf, input);
            SkipHudLayoutPass(host);
            // -slowtext: draw HUD text with GUI.Label as before, to compare look and cost.
            if (System.Array.Exists(System.Environment.GetCommandLineArgs(), a => string.Equals(a, "-slowtext", System.StringComparison.OrdinalIgnoreCase))) HudDraw.FastText = false;
            float sizeX = (float)(grid.SizeX * SubGrid.SubSize), sizeZ = (float)(grid.SizeZ * SubGrid.SubSize);
            Benchmark.InstallIfRequested(host, perf, player, rig, new Vector3(sizeX * 0.5f, 0f, sizeZ * 0.5f), Mathf.Min(sizeX, sizeZ));
        }
    }
}
