using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Bootstrap
{
    /// <summary>The single object in the Bootstrap scene. Builds everything else from code.</summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            Time.fixedDeltaTime = 1f / SimTime.TicksPerSecond;
            Debug.Log($"[SCULPT] Bootstrap OK. Sim tick rate {SimTime.TicksPerSecond} Hz.");
        }
    }
}
