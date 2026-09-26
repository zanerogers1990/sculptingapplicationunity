using UnityEngine;

namespace Sculpting
{
    /// One live mirror copy of a MirrorRepeater's object - render-only: a MeshFilter sharing the
    /// original's mesh, a MeshRenderer, and this. No collider and no SculptableMesh; everything
    /// that works on it goes through the original.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class MirrorRepeaterView : MonoBehaviour
    {
        [SerializeField] private MirrorRepeater owner;
        [SerializeField] private Vector3 signs = Vector3.one;
        [SerializeField] private string axisName;

        private MeshFilter _filter;
        private MeshRenderer _renderer;

        public MirrorRepeater Owner => owner;
        /// -1 on each axis this copy is reflected across.
        public Vector3 Signs => signs;
        public string AxisName => axisName;
        public MeshFilter Filter => _filter != null ? _filter : (_filter = GetComponent<MeshFilter>());
        public MeshRenderer Renderer => _renderer != null ? _renderer : (_renderer = GetComponent<MeshRenderer>());

        internal void Init(MirrorRepeater repeater, Vector3 mirrorSigns, string name)
        {
            owner = repeater;
            signs = mirrorSigns;
            axisName = name;
        }
    }
}
