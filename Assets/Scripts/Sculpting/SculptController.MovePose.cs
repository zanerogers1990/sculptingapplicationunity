using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// The Move and Pose brushes: whole-drag grabs rather than paced dabs, plus the pose chain visual.
    public partial class SculptController
    {
        private bool _isMoveDragging;

        private Vector3 _dragPlanePoint;

        private Vector3 _dragPlaneNormal;

        private Vector3 _lastDragPoint;

        // One selection per active mirror sign, paired with the sign used to make it, so a
        // drag delta can be re-mirrored before being applied to that selection.
        private List<(SculptableMesh.GrabSelection selection, Vector3 sign)> _grabSelections;

        // Pose brush - see HandlePoseInput. Same click/hold-drag/release shape as Move above,
        // right down to reusing RayPlaneIntersect against a camera-facing plane through the
        // grab point, but each mirrored selection is a PoseSelection (a chain, not a flat
        // weighted blob) and the drag target is read fresh from that plane every frame rather
        // than accumulated - see SculptableMesh.ApplyPoseDelta's remarks on why.
        private bool _isPoseDragging;

        private Vector3 _poseDragPlanePoint;

        private Vector3 _poseDragPlaneNormal;

        private List<(SculptableMesh.PoseSelection selection, Vector3 sign)> _poseSelections;

        // Guide line (see UpdatePoseChainVisual): a world-space LineRenderer per active pose
        // selection - up to one per mirror sign, matching _poseSelections. First version used
        // Sprites/Default with ordinary depth testing and was reported as barely-visible - "maybe
        // a slight outline" - because the chain runs directly along the mesh's own surface, which
        // z-fights against that same surface almost everywhere except where floating-point noise
        // happens to let it win. Custom/BrushPreviewOverlay (ZTest Always, ZWrite Off) is this
        // project's existing fix for exactly that problem - TransformGizmo's handles and the
        // brush-preview cursor already draw on top of everything for the identical reason (see
        // TransformGizmo.ApplyUnlitColor's remarks) - so this reuses it rather than re-solving it.
        private static Material _poseChainMaterial;

        private static readonly int PoseChainColorId = Shader.PropertyToID("_Color");

        private readonly List<LineRenderer> _poseChainLines = new List<LineRenderer>();

        private static readonly Color PoseChainColor = Color.white;

        // Fraction of brushRadius, not an absolute width - brushRadius is already the one value
        // in scope that tracks how big the CURRENT model/selection is (an artist sets it relative
        // to their own mesh's scale, same as every other brush), so a line this thin relative to
        // it reads consistently thin whether the mesh is a 1-unit test sphere or a 2-meter figure
        // - a fixed absolute width would have been fine for the former and invisible on the
        // latter, which is the other half of why the first version read as barely-there.
        private const float PoseChainWidthFactor = 0.03f;

        // Grabs whatever's under the cursor on mouse-down and drags it with the cursor's
        // world-space movement along a camera-facing plane through the grab point, instead of
        // re-raycasting the mesh every frame. That's what makes it keep tracking once the
        // cursor moves past the mesh's silhouette, and gives 1:1 "pull" instead of a slow
        // per-frame nudge along a fixed normal.
        private void HandleMoveDrag(Mouse mouse, bool overUI, bool altHeld)
        {
            if (_isMoveDragging)
            {
                if (!mouse.leftButton.isPressed)
                {
                    EndActiveDrags();
                    return;
                }

                Ray dragRay = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (RayPlaneIntersect(dragRay, _dragPlanePoint, _dragPlaneNormal, out Vector3 current))
                {
                    Vector3 worldDelta = current - _lastDragPoint;
                    if (worldDelta.sqrMagnitude > 1e-12f)
                    {
                        Vector3 localDelta = sculptableMesh.transform.InverseTransformVector(worldDelta);
                        BeginDirtyVertices();
                        foreach (var (selection, sign) in _grabSelections)
                        {
                            sculptableMesh.ApplyGrabDelta(selection, Vector3.Scale(localDelta, sign));
                            int[] indices = selection.Indices;
                            for (int k = 0; k < indices.Length; k++) _dirtyVertexScratch.Add(indices[k]);
                        }
                        MarkPositionMirrorStale();
                        FlushDirtyVertices();
                    }
                    _lastDragPoint = current;
                }

                _isHovering = true;
                _hoverPoint = _lastDragPoint;
                _previewPositive = true;
                return;
            }

            // Not dragging: only start one on a fresh click while actually hovering the mesh.
            _isHovering = false;
            if (overUI || altHeld) return;

            Ray hoverRay = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(hoverRay, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);
            _isHovering = hasHit;
            if (_isHovering)
            {
                _hoverPoint = hitPoint;
                _hoverNormal = hitNormal;
                _previewPositive = true;
            }

            if (!_isHovering || !mouse.leftButton.wasPressedThisFrame) return;

            Vector3 localHit = sculptableMesh.transform.InverseTransformPoint(hitPoint);

            var selections = new List<(SculptableMesh.GrabSelection, Vector3)>();
            foreach (Vector3 sign in MirrorSigns())
            {
                // Each mirrored selection is judged from its OWN mirrored viewpoint - see
                // _dabCameraLocal. A grab is picked once and then dragged for the whole gesture, so
                // getting this wrong here strands half of the far side's vertices behind for the
                // entire drag rather than merely weakening one frame of it.
                BeginMirroredDab(sign);
                var selection = sculptableMesh.SelectGrab(Vector3.Scale(localHit, sign), brushRadius, frontFacingOnly, _dabCameraLocal,
                    moveConnectedOnly);
                if (selection.IsValid) selections.Add((selection, sign));
            }
            if (selections.Count == 0) return;
            _grabSelections = selections;

            _isMoveDragging = true;
            _dragPlanePoint = hitPoint;
            _dragPlaneNormal = -cam.transform.forward;
            _lastDragPoint = hitPoint;

            if (logRayHits) Debug.Log($"[Sculpt] Move grab started at {hitPoint}");
        }

        private void EndMoveDrag()
        {
            if (!_isMoveDragging) return;
            _grabSelections = null;
            _isMoveDragging = false;
        }

        // Same click/hold-drag/release shape as HandleMoveDrag above, right down to reusing
        // RayPlaneIntersect against a camera-facing plane through the grab point. The
        // difference is entirely in what SelectPose/ApplyPoseDelta do with that drag target -
        // see SculptableMesh's Pose brush section for the actual chain/falloff logic. Masking
        // works the same direction here as every other brush (masked = protected): to pose an
        // arm, mask everything EXCEPT the arm, then drag from inside the unmasked part.
        private void HandlePoseInput(Mouse mouse, bool overUI, bool altHeld)
        {
            if (_isPoseDragging)
            {
                if (!mouse.leftButton.isPressed)
                {
                    EndActiveDrags();
                    return;
                }

                Ray dragRay = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (RayPlaneIntersect(dragRay, _poseDragPlanePoint, _poseDragPlaneNormal, out Vector3 current))
                {
                    // Unlike Move, this reads the CURRENT drag point fresh off the plane rather
                    // than accumulating a delta - ApplyPoseDelta re-derives every vertex from
                    // its stroke-start position each frame, so there's nothing to accumulate
                    // onto (see its remarks for why that's the more robust choice for a
                    // rotation-based deform).
                    Vector3 localCurrent = sculptableMesh.transform.InverseTransformPoint(current);
                    BeginDirtyVertices();
                    foreach (var (selection, sign) in _poseSelections)
                    {
                        sculptableMesh.ApplyPoseDelta(selection, Vector3.Scale(localCurrent, sign));
                        int[] indices = selection.Indices;
                        for (int k = 0; k < indices.Length; k++) _dirtyVertexScratch.Add(indices[k]);
                    }
                    MarkPositionMirrorStale();
                    FlushDirtyVertices();
                }

                _isHovering = true;
                _hoverPoint = current;
                _previewPositive = true;
                return;
            }

            // Not dragging: still recompute the chain every hover frame from wherever the
            // cursor currently is, so the guide line tracks the cursor live - Blender does the
            // same, and it's what actually lets you see what you're about to grab before you
            // commit to a drag. Only the CLICK below promotes an already-fresh selection into an
            // active drag; nothing about the selection itself is special-cased for the click.
            _isHovering = false;
            if (overUI || altHeld) { _poseSelections = null; return; }

            Ray hoverRay = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(hoverRay, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);
            _isHovering = hasHit;
            if (!_isHovering) { _poseSelections = null; return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            _previewPositive = true;

            Vector3 localHit = sculptableMesh.transform.InverseTransformPoint(hitPoint);
            var selections = new List<(SculptableMesh.PoseSelection, Vector3)>();
            foreach (Vector3 sign in MirrorSigns())
            {
                var selection = sculptableMesh.SelectPose(Vector3.Scale(localHit, sign), brushRadius, poseRigidity, poseSegments);
                if (selection.IsValid) selections.Add((selection, sign));
            }
            _poseSelections = selections.Count > 0 ? selections : null;

            if (!mouse.leftButton.wasPressedThisFrame) return;

            if (_poseSelections == null)
            {
                // Clicked outside any unmasked region, or the unmasked island has no masked
                // neighbor anywhere to anchor against - Pose has nothing sensible to do, same
                // as Move missing the mesh entirely. The toast is worth it here specifically
                // because the reason for "nothing happened" (mask setup, not a missed click)
                // is not otherwise visible.
                TriggerActionToast("Mask an anchor first");
                return;
            }

            _isPoseDragging = true;
            _poseDragPlanePoint = hitPoint;
            _poseDragPlaneNormal = -cam.transform.forward;

            if (logRayHits) Debug.Log($"[Sculpt] Pose grab started at {hitPoint}");
        }

        // Unconditional (no _isPoseDragging guard) - unlike EndMoveDrag, _poseSelections is also
        // the live hover-preview's own state (see HandlePoseInput), not exclusively an active-
        // drag flag, so this needs to clear it even when called mid-hover (e.g. switching away
        // from Pose to another brush - see EndActiveDrags' call sites) rather than only at the
        // end of an actual drag.
        private void EndPoseDrag()
        {
            _poseSelections = null;
            _isPoseDragging = false;
        }

        // Blender-style Pose Brush guide line: live from the moment the cursor hovers a posable
        // region with Pose selected, through the drag, same as Blender's own - not just while a
        // drag is underway (see HandlePoseInput, which now keeps _poseSelections fresh on every
        // hover frame too). Called from Update() every frame the app runs, not just while
        // hovering/posing - the early-out below is what keeps that free the rest of the time.
        private void UpdatePoseChainVisual()
        {
            if (_poseSelections == null || sculptableMesh == null)
            {
                for (int i = 0; i < _poseChainLines.Count; i++)
                    if (_poseChainLines[i] != null) _poseChainLines[i].gameObject.SetActive(false);
                return;
            }

            while (_poseChainLines.Count < _poseSelections.Count) _poseChainLines.Add(CreatePoseChainLine());

            float width = Mathf.Max(brushRadius * PoseChainWidthFactor, 0.0005f);
            for (int s = 0; s < _poseSelections.Count; s++)
            {
                var (selection, sign) = _poseSelections[s];
                LineRenderer lr = _poseChainLines[s];
                Vector3[] points = selection.ChainPoints;
                if (points == null || points.Length < 2) { lr.gameObject.SetActive(false); continue; }

                lr.gameObject.SetActive(true);
                lr.widthMultiplier = width;
                lr.positionCount = points.Length;
                for (int p = 0; p < points.Length; p++)
                    lr.SetPosition(p, sculptableMesh.transform.TransformPoint(Vector3.Scale(points[p], sign)));
            }
            for (int s = _poseSelections.Count; s < _poseChainLines.Count; s++)
                _poseChainLines[s].gameObject.SetActive(false);
        }

        private LineRenderer CreatePoseChainLine()
        {
            var go = new GameObject("PoseChainLine");
            go.transform.SetParent(transform, false);

            if (_poseChainMaterial == null)
            {
                // Same overlay-shader-with-Sprites/Default-fallback dance as
                // TransformGizmo.ApplyUnlitColor - see this field's own remarks for why the
                // overlay shader is what actually matters here.
                Shader overlayShader = Shader.Find("Custom/BrushPreviewOverlay");
                _poseChainMaterial = overlayShader != null ? new Material(overlayShader) : new Material(Shader.Find("Sprites/Default"));
                _poseChainMaterial.name = "Pose Chain Line (Runtime)";
                if (overlayShader != null) _poseChainMaterial.SetColor(PoseChainColorId, PoseChainColor);
                else _poseChainMaterial.color = PoseChainColor;
            }

            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _poseChainMaterial;
            lr.useWorldSpace = true;
            lr.numCapVertices = 4;
            lr.numCornerVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            go.SetActive(false);
            return lr;
        }
    }
}
