using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game
{
    /// <summary>Core and Unity share axes: X = east, Y = up, Z = north. Only precision differs.</summary>
    public static class CoreConvert
    {
        public static Vector3 ToUnity(this Vec3d v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
        public static Vec3d ToCore(this Vector3 v) => new Vec3d(v.x, v.y, v.z);
        public static Vector3 ToUnity(this Int3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
