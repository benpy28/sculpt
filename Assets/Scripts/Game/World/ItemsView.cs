using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Weapons;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Draws the items on the ground from the local client's copy (the server's list). Guns lie on their side, grenade
    /// stacks show one grenade per count. The one under your crosshair lifts a little and turns slowly so you can see
    /// what interact will grab. One GameObject per item (there are at most a few dozen).
    /// </summary>
    public sealed class ItemsView : MonoBehaviour
    {
        private NetSession _session;
        private LocalPlayer _player;
        private Material _mat;
        private int _version = -1;
        private NetClient _boundTo;
        private readonly Dictionary<int, Transform> _shown = new Dictionary<int, Transform>();
        private Sculpt.Core.Settings.ColourMode _colours = Sculpt.Game.UI.RarityColours.Mode;
        private readonly HashSet<int> _alive = new HashSet<int>();
        private readonly List<int> _gone = new List<int>();

        public void Init(NetSession session, LocalPlayer player)
        {
            _session = session;
            _player = player;
            Shader s = Shader.Find("SCULPT/Viewmodel");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Unlit");
            _mat = new Material(s) { name = "Ground items" };
        }

        private void LateUpdate()
        {
            NetClient net = _session.Client;
            if (net != _boundTo) { _boundTo = net; _version = -1; }
            GroundItems items = net.Items;
            if (Sculpt.Game.UI.RarityColours.Mode != _colours)
            {
                // Colourblind mode changed: the rarity plates are baked in their colour, so rebuild every item.
                _colours = Sculpt.Game.UI.RarityColours.Mode;
                foreach (var kv in _shown) Destroy(kv.Value.gameObject);
                _shown.Clear();
                _version = -1;
            }
            if (items.Version != _version)
            {
                _version = items.Version;
                Sync(items);
            }

            int target = _player.PickTarget;
            int targetId = target >= 0 && target < items.Count ? items[target].Id : 0;
            for (int i = 0; i < items.Count; i++)
            {
                GroundItem g = items[i];
                if (!_shown.TryGetValue(g.Id, out Transform t)) continue;
                bool hot = g.Id == targetId;
                Vector3 basePos = g.Position.ToUnity();
                float yaw = (g.Id * 47) % 360;
                if (g.Kind == Item.Chest) { yaw = (float)(NetServer.ChestYaw(g) * Mathf.Rad2Deg); hot = false; } // chests sit square and face their way
                if (hot) yaw += Time.time * 60f;
                t.position = basePos + new Vector3(0, hot ? 0.08f + Mathf.Sin(Time.time * 4f) * 0.015f : 0f, 0);
                t.rotation = Quaternion.Euler(0, yaw, 0);
            }
        }

        private void Sync(GroundItems items)
        {
            _alive.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                GroundItem g = items[i];
                _alive.Add(g.Id);
                if (_shown.TryGetValue(g.Id, out Transform old))
                {
                    if (old.childCount == Visuals(g)) continue;
                    Destroy(old.gameObject); // a stack changed size: rebuild it
                    _shown.Remove(g.Id);
                }
                _shown[g.Id] = Build(g);
            }
            _gone.Clear();
            foreach (var kv in _shown) if (!_alive.Contains(kv.Key)) _gone.Add(kv.Key);
            foreach (int id in _gone)
            {
                Destroy(_shown[id].gameObject);
                _shown.Remove(id);
            }
        }

        private static int Visuals(in GroundItem g) => (g.Kind == Item.Grenade || g.Kind == Item.C4 || Items.IsHeal(g.Kind) ? Mathf.Clamp(g.Count, 1, 4) : 1) + (g.Rarity != Rarity.None ? 1 : 0);

        private Transform Build(in GroundItem g)
        {
            var root = new GameObject($"Item {g.Id} {Items.Name(g.Kind)}").transform;
            root.SetParent(transform, false);
            if (g.Kind == Item.Chest)
            {
                GameObject chest = GunModels.BuildChest(_mat);
                chest.transform.SetParent(root, false);
                return root;
            }
            if (g.Rarity != Rarity.None)
            {
                GameObject plate = GunModels.BuildRarityPlate(_mat, Sculpt.Game.UI.RarityColours.Of(g.Rarity));
                plate.transform.SetParent(root, false);
            }
            if (Items.IsGun(g.Kind))
            {
                GunModel m = GunModels.Build(Items.GunOf(g.Kind), _mat);
                Transform gun = m.Root.transform;
                gun.SetParent(root, false);
                // Lying on its right side, centred on the item point, resting on the floor.
                gun.localRotation = Quaternion.Euler(0, 0, 90);
                gun.localPosition = new Vector3(0, 0.04f, -0.05f);
                gun.localScale = Vector3.one * 1.15f; // a touch bigger than real so it reads from standing height
            }
            else if (g.Kind == Item.Launcher)
            {
                GunModel m = GunModels.BuildLauncher(_mat);
                Transform gun = m.Root.transform;
                gun.SetParent(root, false);
                gun.localRotation = Quaternion.Euler(0, 0, 90);
                gun.localPosition = new Vector3(0, 0.05f, -0.08f);
                gun.localScale = Vector3.one * 1.15f;
            }
            else if (g.Kind == Item.C4)
            {
                int n = Mathf.Clamp(g.Count, 1, 4);
                for (int k = 0; k < n; k++)
                {
                    GameObject c4 = GunModels.BuildC4(_mat);
                    c4.transform.SetParent(root, false);
                    c4.transform.localPosition = new Vector3(0, 0.035f + k * 0.07f, 0); // stacked bricks
                    c4.transform.localRotation = Quaternion.Euler(0, k * 17f, 0);
                    c4.transform.localScale = Vector3.one * 1.2f;
                }
            }
            else if (g.Kind == Item.Bandage)
            {
                int n = Mathf.Clamp(g.Count, 1, 4);
                for (int k = 0; k < n; k++)
                {
                    GameObject roll = GunModels.BuildBandage(_mat);
                    roll.transform.SetParent(root, false);
                    roll.transform.localPosition = new Vector3((k - (n - 1) * 0.5f) * 0.1f, 0.04f, 0);
                    roll.transform.localRotation = Quaternion.Euler(0, k * 23f, 0);
                    roll.transform.localScale = Vector3.one * 1.4f;
                }
            }
            else if (g.Kind == Item.Medkit)
            {
                int n = Mathf.Clamp(g.Count, 1, 4);
                for (int k = 0; k < n; k++)
                {
                    GameObject kit = GunModels.BuildMedkit(_mat);
                    kit.transform.SetParent(root, false);
                    kit.transform.localPosition = new Vector3(0, k * 0.13f, 0); // stacked cases
                    kit.transform.localRotation = Quaternion.Euler(0, k * 11f, 0);
                    kit.transform.localScale = Vector3.one * 1.2f;
                }
            }
            else
            {
                int n = Mathf.Clamp(g.Count, 1, 4);
                for (int k = 0; k < n; k++)
                {
                    GameObject nade = GunModels.BuildGrenade(_mat);
                    nade.transform.SetParent(root, false);
                    nade.transform.localPosition = new Vector3((k - (n - 1) * 0.5f) * 0.09f, 0.045f, 0);
                    nade.transform.localScale = Vector3.one * 1.3f;
                }
            }
            return root;
        }
    }
}
