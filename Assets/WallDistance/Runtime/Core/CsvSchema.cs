using System.Collections.Generic;

namespace WallDistance.Core
{
    /// <summary>
    /// Names of the CSV columns added for learned detection (spec §7), in file order. They live in
    /// Core so a test pins them: tools/bench and tools/field read these names, and a silent
    /// rename would break the benchmark analysis without failing any build.
    /// </summary>
    public static class CsvSchema
    {
        public const string Revision = "floor-aligned-v1";

        /// <summary>Appended directly after the existing last column, "depthError".</summary>
        public static readonly string[] LearnedColumns =
        {
            "infer_ms", "infer_hz", "align_s", "align_t", "align_residual", "floor_inliers",
            "walls_in_map", "aimed_source_chain", "edge_snap_frac", "floor_h_m", "thermal_state", "detect_latency_ms",
            // Main-thread cost of the synchronous camera-image conversion (plan deviation D2).
            "prep_ms",
            // Field-debugging diagnostics. Each names a place where a frame can stop silently:
            // the scheduler refusing to submit (e.g. pose/image clock mismatch), the NPU backend
            // (not loaded / failed), and the pipeline discarding a late or other-session result.
            "sched_status", "infer_status", "frames_dropped",
            // Session counts of camera-pose and floor-level jumps (PoseJumpGuard, FloorSelector):
            // how often ARCore's own geometry was not trustworthy during the recording.
            "pose_jumps", "floor_jumps",
            // Wall-map integrity: clears on lost tracking or pose jumps (MapContinuityGuard) and
            // walls removed because the camera saw past them (FreeSpaceCarver).
            "map_clears", "walls_carved",
        };

        /// <summary>The per-reading set, the same shape as the existing aimed_/nearest_ columns.</summary>
        public static string[] ReadingColumns(string prefix, bool withReason)
        {
            var names = new List<string> { "valid", "raw_m", "filtered_m", "source", "quality", "failure", "candidate", "depthResidual_m", "depthInlier" };
            if (withReason) names.Add("reason");
            for (int i = 0; i < names.Count; i++) names[i] = prefix + "_" + names[i];
            return names.ToArray();
        }

        public static string[] AppendedColumns()
        {
            var all = new List<string>(LearnedColumns);
            all.AddRange(ReadingColumns("left", true));
            all.AddRange(ReadingColumns("right", true));
            all.Add("corridor_width_m");
            return all.ToArray();
        }
    }
}
