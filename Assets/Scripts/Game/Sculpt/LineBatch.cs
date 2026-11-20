using System.Collections.Generic;
using UnityEngine;

namespace Sculpt.Game.Sculpting
{
    /// <summary>Thick world-space lines as camera-facing quads, rebuilt into one mesh per frame.</summary>
    public sealed class LineBatch
    {
        private readonly Mesh _mesh;
        private readonly List<Vector3> _v = new List<Vector3>(4096);
        private readonly List<Color> _c = new List<Color>(4096);
        private readonly List<int> _t = new List<int>(6144);
        private Vector3 _cam;
        public readonly GameObject Object;

        public LineBatch(string name, Material material)
        {
            Object = new GameObject(name);
            _mesh = new Mesh { name = name };
            _mesh.MarkDynamic();
            Object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = Object.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        public void Begin(Vector3 cameraPosition)
        {
            _cam = cameraPosition;
            _v.Clear();
            _c.Clear();
            _t.Clear();
        }

        public void Add(Vector3 a, Vector3 b, Color color, float width)
        {
            Vector3 mid = (a + b) * 0.5f;
            Vector3 along = b - a;
            Vector3 side = Vector3.Cross(along, _cam - mid);
            if (side.sqrMagnitude < 1e-10f) return;
            side = side.normalized * (width * 0.5f);
            int i = _v.Count;
            _v.Add(a - side); _v.Add(a + side); _v.Add(b + side); _v.Add(b - side);
            _c.Add(color); _c.Add(color); _c.Add(color); _c.Add(color);
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        public void End()
        {
            _mesh.Clear();
            if (_v.Count == 0) return;
            _mesh.indexFormat = _v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetTriangles(_t, 0, false);
            _mesh.bounds = new Bounds(_cam, Vector3.one * 2000f);
        }
    }
}
