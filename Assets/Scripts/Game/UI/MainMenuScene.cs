using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Game.Cameras;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The home screen's background: a slow camera drift on the 1v1 map looking along the west compound wall (the tall
    /// free-standing brick wall between the west building and the keep), with a blast hole punched through it. The
    /// hole is made on the local server like any tile change (cause Built, so no debris or sound), the client copies
    /// it, and it is put back when the menu closes into free roam. Visual only: nothing here runs during a match.
    /// </summary>
    internal static class MainMenuScene
    {
        // 1v1 map (DuelMap, 96 m): the compound wall runs along X from 16.35 to 32.1 m at z 44.7, three tiles (4.5 m) tall.
        private const float WallZ = 44.7f;
        private static readonly Vector3 HoleCentre = new Vector3(26.2f, 2.0f, WallZ);
        private static readonly Vector3 CameraAt = new Vector3(19.4f, 1.25f, 28.6f);
        private const float CameraYaw = 7f, CameraPitch = -8f, CameraFov = 92f;

        // The blast: overlapping bites (offset along the wall, offset up, radius), so the hole is big, roughly round
        // and uneven, like a grenade went off against it. Then a wider, weaker ring that only cracks the brick.
        private static readonly Vector3[] Bites =
        {
            new Vector3(0f, 0f, 1.6f), new Vector3(1.1f, -0.75f, 1.2f), new Vector3(-1.0f, 0.85f, 1.15f),
            new Vector3(0.9f, 1.15f, 1.0f), new Vector3(-1.25f, -0.7f, 1.0f), new Vector3(1.8f, 0.3f, 0.9f), new Vector3(-1.8f, 0.1f, 0.8f),
        };

        public sealed class MenuCamera : ICameraMode
        {
            private float _t;

            public void UpdatePose(float dt, out Vector3 position, out Quaternion rotation, out float horizontalFov)
            {
                _t += dt;
                // A slow, small drift so the scene feels alive without pulling the eye off the panel.
                position = CameraAt + new Vector3(Mathf.Sin(_t * 0.071f) * 0.45f, Mathf.Sin(_t * 0.13f) * 0.05f, Mathf.Sin(_t * 0.053f) * 0.3f);
                rotation = Quaternion.Euler(CameraPitch + Mathf.Sin(_t * 0.09f) * 0.35f, CameraYaw + Mathf.Sin(_t * 0.047f) * 1.4f, 0f);
                horizontalFov = CameraFov;
            }
        }

        /// <summary>Punches the hole on the server's tiles. Returns the tiles it touched (to put back later), or null.</summary>
        public static List<int> Blast(TileWalls w)
        {
            if (w == null) return null;
            var touched = new List<int>();
            var near = new List<int>();
            w.TilesNear(new Aabb(new Vec3d(HoleCentre.x - 4.5, 0, WallZ - 0.6), new Vec3d(HoleCentre.x + 4.5, 4.6, WallZ + 0.9)), near);
            foreach (int t in near)
                if (!w.IsFollower(t) && w[t].Kind == TileKind.Wall && w[t].State == TileState.Intact) touched.Add(t);
            if (touched.Count == 0) return null; // not the 1v1 map (or it changed): plain background

            int big = w.Config.IntactHealth * 3;
            foreach (Vector3 b in Bites) Hit(w, touched, HoleCentre.x + b.x, HoleCentre.y + b.y, big, b.z);
            Hit(w, touched, HoleCentre.x, HoleCentre.y, w.Config.IntactHealth / 2 + 5, 2.9f); // cracks around the rim
            return touched;
        }

        private static void Hit(TileWalls w, List<int> tiles, float x, float y, int amount, float radius)
        {
            foreach (int t in tiles)
            {
                Aabb b = w.BoundsOf(t);
                if (x < b.Min.X || x > b.Max.X || y < b.Min.Y || y > b.Max.Y) continue;
                if (w[t].State == TileState.Broken) return;
                w.Damage(t, amount, new Vec3d(x, y, (b.Min.Z + b.Max.Z) * 0.5), TileCause.Built, 0, radius);
                return;
            }
        }

        /// <summary>Puts the blasted tiles back whole.</summary>
        public static void Restore(TileWalls w, List<int> touched)
        {
            if (w == null || touched == null) return;
            foreach (int t in touched)
                if (w[t].State != TileState.Intact || w[t].Health != w.Config.IntactHealth || w[t].Open != 0)
                    w.Set(t, TileState.Intact, w.Config.IntactHealth, TileCause.Built);
        }
    }
}
