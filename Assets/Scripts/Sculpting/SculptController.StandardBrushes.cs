using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Standard, Layer and Snakehook - the three ZBrush staples the original brush set was missing.
    ///
    /// Standard and Layer push the footprint along ONE direction per dab, the footprint's own
    /// averaged normal, where Inflate pushes each vertex along its own normal: that is the
    /// difference between raising a form (Standard) and swelling it until its corners round off
    /// (Inflate). Layer is Standard with a flat top and a fixed height it never exceeds within a
    /// stroke, so crossing your own stroke does not double it - the tool for raising a panel or a
    /// strap by one even thickness.
    public partial class SculptController
    {
        // Standard's Accumulate-ON push rate, per dab-second - the same calibration as
        // InflateAccumulateSpeed, so one default-strength pass raises the stroke's centre by about a
        // quarter of the radius.
        private const float StandardSpeed = 1.25f;
        // Accumulate OFF: how far above the surface the stroke found Standard may build, as a
        // fraction of the radius, and how fast it eases there per dab-second (Inflate's OFF rate).
        private const float StandardOffHeightFactor = 0.3f;
        private const float StandardOffSpeed = 4f;
        // Layer's approach to its height, per dab-second. Fast: at the default strength one pass
        // gets most of the way, which is how ZBrush's Layer reads - a stroke IS the layer.
        private const float LayerSpeed = 40f;
        // The fraction of Layer's radius that is flat-topped at full height. The rest is a
        // smootherstep down to the edge, so the layer's rim blends instead of stepping.
        private const float LayerPlateau = 0.35f;

        private System.Action<Vector3, Vector3, bool, float> _applyStandardBrushLocal;
        private System.Action<Vector3, Vector3, bool, float> _applyLayerBrushLocal;

        internal struct DirectionalSettings
        {
            public float BrushRadius;
            public float Plateau;   // fraction of the radius at full weight
            public bool Capped;     // false: push by Rate * weight; true: ease toward Height * weight
            public float Rate;      // signed displacement per dab at full weight
            public float Height;    // signed cap, measured along the dab direction from the stroke start
            public float Lerp;      // fraction of the remaining distance to the cap covered per dab
            public bool FrontFacingOnly;
            public Vector3 CameraLocalPos;
        }

        /// Smootherstep from the edge of the radius up to the plateau. u is distance / radius.
        internal static float DirectionalFalloff(float u, float plateau)
        {
            float t = Mathf.Clamp01((1f - u) / Mathf.Max(1f - plateau, 1e-4f));
            return BrushFalloff.Apply(1f - u, t * t * t * (t * (t * 6f - 15f) + 10f));
        }

        /// One vertex's move, shared by the job and the managed path. Capped mode never pulls a
        /// vertex back down: a vertex already past this dab's target (raised there by a
        /// neighbouring dab, whose centre was closer) keeps what it has - the rule that turns a row
        /// of overlapping dabs into one layer of even height, the same one Crease uses for depth.
        internal static Vector3 DirectionalStep(Vector3 pos, Vector3 strokeStart, float weight, Vector3 dir,
            in DirectionalSettings s)
        {
            if (!s.Capped) return pos + dir * (s.Rate * weight);

            float target = s.Height * weight;
            float achieved = Vector3.Dot(pos - strokeStart, dir);
            float remaining = target - achieved;
            if (remaining * s.Height <= 0f) return pos;
            return pos + dir * (remaining * s.Lerp);
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct DirectionalJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            [ReadOnly] public NativeArray<Vector3> StrokeStartIn;
            public NativeArray<Vector3> PositionsOut;
            public NativeArray<byte> AppliedOut;

            public Vector3 LocalPoint;
            public Vector3 Direction;
            public DirectionalSettings Settings;

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                float dist = Vector3.Distance(pos, LocalPoint);
                if (dist > Settings.BrushRadius) { AppliedOut[index] = 0; return; }

                float weight = DirectionalFalloff(dist / Settings.BrushRadius, Settings.Plateau) * (1f - MaskIn[index])
                    * FrontFacingWeight(Settings.FrontFacingOnly, NormalsIn[index], pos, Settings.CameraLocalPos);
                if (weight <= 0f) { AppliedOut[index] = 0; return; }

                PositionsOut[index] = DirectionalStep(pos, StrokeStartIn[index], weight, Direction, Settings);
                AppliedOut[index] = 1;
            }
        }

        private void ApplyStandardBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive, float dt)
        {
            float sign = positive ? 1f : -1f;
            var s = new DirectionalSettings
            {
                BrushRadius = brushRadius,
                Plateau = 0f,
                Capped = !accumulate,
                Rate = sign * EffectiveDabStrengthAccumulate * StandardSpeed * dt * RadiusScale,
                Height = sign * brushRadius * StandardOffHeightFactor,
                Lerp = Mathf.Clamp01(EffectiveDabStrength * StandardOffSpeed * dt),
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = _dabCameraLocal,
            };
            ApplyDirectionalBrushLocal(localPoint, localNormal, s);
        }

        private void ApplyLayerBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive, float dt)
        {
            float sign = positive ? 1f : -1f;
            var s = new DirectionalSettings
            {
                BrushRadius = brushRadius,
                Plateau = LayerPlateau,
                Capped = true, // Layer ignores Accumulate: a fixed height is the whole brush
                Height = sign * brushRadius * layerHeight,
                Lerp = Mathf.Clamp01(EffectiveDabStrength * LayerSpeed * dt),
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = _dabCameraLocal,
            };
            ApplyDirectionalBrushLocal(localPoint, localNormal, s);
        }

        private void ApplyDirectionalBrushLocal(Vector3 localPoint, Vector3 direction, in DirectionalSettings s)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyDirectionalBrushLocalJob(localPoint, direction, s, candidates, verts);
            else
                ApplyDirectionalBrushLocalManaged(localPoint, direction, s, candidates, verts);
        }

        private void ApplyDirectionalBrushLocalJob(Vector3 localPoint, Vector3 direction, in DirectionalSettings s,
            List<int> candidates, Vector3[] verts)
        {
            GatherCandidatesNative(candidates, verts, sculptableMesh.Normals, sculptableMesh.Mask);
            sculptableMesh.CopyStrokeStartPositions(candidates, _nativeStrokeStart);
            var job = new DirectionalJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                StrokeStartIn = _nativeStrokeStart,
                PositionsOut = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                LocalPoint = localPoint,
                Direction = direction,
                Settings = s,
            };
            job.Schedule(candidates.Count, 32).Complete();
            ScatterJobResults(candidates, verts);
        }

        private void ApplyDirectionalBrushLocalManaged(Vector3 localPoint, Vector3 direction, in DirectionalSettings s,
            List<int> candidates, Vector3[] verts)
        {
            SculptableMesh mesh = sculptableMesh;
            float[] mask = mesh.Mask;
            Vector3[] normals = mesh.Normals;
            float radiusSqr = s.BrushRadius * s.BrushRadius;
            float invRadius = 1f / s.BrushRadius;
            bool anyMoved = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 p = verts[i];
                float sqrDist = (p - localPoint).sqrMagnitude;
                if (sqrDist > radiusSqr) continue;

                float weight = DirectionalFalloff(Mathf.Sqrt(sqrDist) * invRadius, s.Plateau) * (1f - mask[i])
                    * FrontFacingWeight(s.FrontFacingOnly, normals[i], p, s.CameraLocalPos);
                if (weight <= 0f) continue;

                mesh.RecordUndoBeforeIfNeeded(i);
                anyMoved = true;
                verts[i] = DirectionalStep(p, mesh.StrokeStartPosition(i), weight, direction, s);
                _dirtyVertexScratch.Add(i);
            }

            if (anyMoved) MarkPositionMirrorStale();
        }

        // ------------------------------------------------------------------ Snakehook

        // Drags the surface under the brush along with the cursor, re-grabbing at the tip every
        // step - where Move grabs once and drags that same patch for the whole gesture. Repeatedly
        // grabbing what is now at the tip is what pulls a horn, a tentacle or a finger out of the
        // surface instead of just denting it sideways.
        private bool _isSnakeDragging;
        private Vector3 _snakeTipWorld;
        private Vector3 _snakePlaneNormal;

        // A long frame's travel is split into steps no longer than this fraction of the radius, so
        // the re-grab keeps up with the tip: in one big step the vertices the tip is about to pass
        // over would be left behind.
        private const float SnakeStepFraction = 0.15f;
        private const int SnakeMaxStepsPerFrame = 64;

        private void HandleSnakeHookInput(Mouse mouse, bool overUI, bool altHeld)
        {
            if (_isSnakeDragging)
            {
                if (!mouse.leftButton.isPressed) { EndSnakeDrag(); return; }

                Ray dragRay = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
                if (RayPlaneIntersect(dragRay, _snakeTipWorld, _snakePlaneNormal, out Vector3 current))
                {
                    Vector3 worldDelta = current - _snakeTipWorld;
                    if (worldDelta.sqrMagnitude > 1e-12f) ApplySnakeHook(worldDelta);
                }

                _isHovering = true;
                _hoverPoint = _snakeTipWorld;
                _previewPositive = true;
                return;
            }

            _isHovering = false;
            if (overUI || altHeld) return;

            Ray hoverRay = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            if (!sculptableMesh.RaycastMesh(hoverRay, 1000f, out Vector3 hitPoint, out Vector3 hitNormal)) return;
            _isHovering = true;
            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            _previewPositive = true;

            if (!mouse.leftButton.wasPressedThisFrame) return;
            _isSnakeDragging = true;
            _snakeTipWorld = hitPoint;
            // Camera-facing, like Move: the tip follows the cursor across the screen, and orbiting
            // between strokes is how you pull in depth.
            _snakePlaneNormal = -cam.transform.forward;
        }

        private void EndSnakeDrag() => _isSnakeDragging = false;

        private void ApplySnakeHook(Vector3 worldDelta)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localTip = t.InverseTransformPoint(_snakeTipWorld);
            Vector3 localDelta = t.InverseTransformVector(worldDelta);

            float maxStep = Mathf.Max(brushRadius * SnakeStepFraction, 1e-5f);
            int steps = Mathf.Clamp(Mathf.CeilToInt(localDelta.magnitude / maxStep), 1, SnakeMaxStepsPerFrame);
            Vector3 step = localDelta / steps;

            BeginDirtyVertices();
            float travelled = 0f;
            for (int k = 0; k < steps; k++)
            {
                // The vertex index still files moved vertices under where they were at the start of
                // this frame (it is refreshed once, at the flush), so widen each query by how far
                // this frame has already carried them.
                float pad = travelled;
                MirroredDabWalk dabs = BeginMirroredDabs(localTip, brushRadius + pad);
                while (NextMirroredDab(ref dabs, out Vector3 sign))
                    SnakeHookStep(Vector3.Scale(localTip, sign), Vector3.Scale(step, sign), pad);
                localTip += step;
                travelled += step.magnitude;
            }
            FlushDirtyVertices();

            _snakeTipWorld += worldDelta;
        }

        private void SnakeHookStep(Vector3 tip, Vector3 delta, float queryPad)
        {
            SculptableMesh mesh = sculptableMesh;
            Vector3[] verts = mesh.Vertices;
            Vector3[] normals = mesh.Normals;
            float[] mask = mesh.Mask;
            List<int> candidates = mesh.QueryNear(tip, brushRadius + queryPad);
            float radiusSqr = brushRadius * brushRadius;
            float invRadius = 1f / brushRadius;
            bool anyMoved = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 p = verts[i];
                float sqrDist = (p - tip).sqrMagnitude;
                if (sqrDist > radiusSqr) continue;

                float t01 = 1f - Mathf.Sqrt(sqrDist) * invRadius;
                float weight = BrushFalloff.Apply(t01, t01 * t01 * (3f - 2f * t01)) * (1f - mask[i])
                    * FrontFacingWeight(frontFacingOnly, normals[i], p, _dabCameraLocal);
                if (weight <= 0f) continue;

                mesh.RecordUndoBeforeIfNeeded(i);
                verts[i] = p + delta * weight;
                _dirtyVertexScratch.Add(i);
                anyMoved = true;
            }

            if (anyMoved) MarkPositionMirrorStale();
        }
    }
}
