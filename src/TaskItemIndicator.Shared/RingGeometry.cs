using System;

namespace TaskItemIndicator.Shared
{
    /// <summary>
    /// Pure ring geometry and brightness math, split out of <c>TaskItemIndicatorPlugin</c> so it can be
    /// unit tested without a BepInEx/UnityEngine reference or an SPT install. Everything here mirrors
    /// the measurements documented in README.md's "Where the ring came from" section - four arcs on the
    /// diagonals, taper toward each arc's ends, and the bearing/opacity/converge math that decides how
    /// bright each arc is on a given frame.
    ///
    /// Deliberately has no UnityEngine/BepInEx dependency: <see cref="MathF"/> stands in for Unity's
    /// <c>Mathf</c>, and the angle helpers Unity provides (<c>Mathf.DeltaAngle</c>, <c>Mathf.Repeat</c>,
    /// <c>Mathf.Clamp01</c>, <c>Mathf.Lerp</c>) are reimplemented below to match Unity's behaviour
    /// exactly, so the plugin can pass Mathf-derived floats in and get the identical result out.
    /// </summary>
    public static class RingGeometry
    {
        // four arcs on the diagonals, ~70 deg wide, ~20 deg gaps on the cardinals - measured off BSG's
        // reveal clip, three frames across two scenes
        public static readonly float[] SegmentCentres = { 45f, 135f, 225f, 315f };
        public const float SegmentHalfWidth = 35f;

        // Original hand-tuned band width (inner/outer radius). Kept for reference only - the plugin's
        // Ring Thickness config now supplies its own ratio instead of reading this constant.
        public const float InnerRadiusFraction = 4f / 7f;

        public const float EndTaper = 0.72f;
        public const float ReferenceOuterRadius = 7f;
        public const float ReferenceScreenHeight = 720f;
        public const int Supersample = 4;

        private static readonly float DegToRad = MathF.PI / 180f;
        private static readonly float RadToDeg = 180f / MathF.PI;

        /// <summary>Unity's Mathf.Clamp01.</summary>
        public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        /// <summary>Unity's Mathf.Lerp - clamps t to [0,1], unlike LerpUnclamped.</summary>
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        /// <summary>Unity's Mathf.Repeat - wraps t into [0, length).</summary>
        public static float Repeat(float t, float length)
        {
            float value = t - MathF.Floor(t / length) * length;
            return value < 0f ? 0f : value > length ? length : value;
        }

        /// <summary>
        /// Unity's Mathf.DeltaAngle - shortest signed difference between two angles in degrees, in
        /// (-180, 180].
        /// </summary>
        public static float DeltaAngle(float current, float target)
        {
            float delta = Repeat(target - current, 360f);
            if (delta > 180f)
            {
                delta -= 360f;
            }
            return delta;
        }

        /// <summary>
        /// Per-arc target alpha for the current frame - the math from
        /// <c>TaskItemIndicatorPlugin.UpdateArcs</c>. <paramref name="bearingDegrees"/> is 0 = dead
        /// ahead, +90 = to your right, matching <c>Vector3.SignedAngle(forward, toItem, Vector3.up)</c>.
        /// Returns four zeros when there's no target or an interaction prompt is covering the ring.
        /// </summary>
        public static float[] ComputeArcAlphas(
            bool hasTarget,
            bool interactionPromptVisible,
            float bearingDegrees,
            float targetDistance,
            float triggerDistance,
            float convergeDistance,
            float maxOpacity,
            float unlitLevel,
            float directionSharpness,
            float fadeInFraction)
        {
            float[] target = new float[4];

            if (!hasTarget || interactionPromptVisible)
            {
                return target;
            }

            float closeness = 1f - Clamp01(targetDistance / triggerDistance);
            float opacity = Clamp01(closeness / fadeInFraction) * maxOpacity;

            // the all-arcs-lit state is for standing on the item, so it keys off an absolute distance
            // rather than a fraction of the trigger radius - tie it to the radius and it starts washing
            // out the direction the moment you enter it. Uniform needs BOTH close and facing it, or
            // turning away inside the zone leaves you with a blank ring exactly when you still haven't
            // spotted the thing. Squared so it only really lets go when you are looking near enough
            // straight at it
            float band = MathF.Max(0.01f, convergeDistance);
            float facing = MathF.Max(0f, MathF.Cos(bearingDegrees * DegToRad));
            float converge = Clamp01((band * 2f - targetDistance) / band) * facing * facing;

            for (int i = 0; i < 4; i++)
            {
                float delta = DegToRad * DeltaAngle(bearingDegrees, SegmentCentres[i]);
                float aligned = MathF.Pow(MathF.Max(0f, MathF.Cos(delta)), directionSharpness);
                float lit = unlitLevel + (1f - unlitLevel) * aligned;
                target[i] = opacity * Clamp01(Lerp(lit, 1f, converge));
            }

            return target;
        }

        /// <summary>
        /// Coverage contribution of one supersampled pixel toward one arc - the inner-loop math from
        /// <c>TaskItemIndicatorPlugin.BuildArcTexture</c>. <paramref name="x"/>/<paramref name="y"/> are
        /// supersampled pixel coordinates, <paramref name="centre"/> is the texture's centre in the same
        /// space. Returns false (taper 0) for pixels outside the ring band or outside this arc's angular
        /// span.
        /// </summary>
        public static bool TryGetArcTaper(
            float x, float y, float centre, float outerRadius, float innerRadius, float centreDegrees,
            out float taper)
        {
            float dx = x - centre;
            float dy = y - centre;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d > outerRadius || d < innerRadius)
            {
                taper = 0f;
                return false;
            }

            // 0 deg = up, clockwise. +dy is up because Texture2D row 0 is the BOTTOM row - negating it
            // here mirrors every arc vertically, which is invisible on a ring this symmetric and only
            // shows once one arc lights on its own
            float angle = Repeat(MathF.Atan2(dx, dy) * RadToDeg, 360f);
            float off = MathF.Abs(DeltaAngle(angle, centreDegrees));
            if (off > SegmentHalfWidth)
            {
                taper = 0f;
                return false;
            }

            taper = 1f - (1f - EndTaper) * (off / SegmentHalfWidth);
            return true;
        }
    }
}
