using NUnit.Framework;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class CsvSchemaTests
    {
        [Test]
        public void ReadingColumns_MirrorTheExistingAimedSet()
        {
            CollectionAssert.AreEqual(new[]
            {
                "aimed_valid", "aimed_raw_m", "aimed_filtered_m", "aimed_source", "aimed_quality", "aimed_failure", "aimed_candidate",
                "aimed_depthResidual_m", "aimed_depthInlier", "aimed_reason",
            }, CsvSchema.ReadingColumns("aimed", true));
            Assert.AreEqual(9, CsvSchema.ReadingColumns("nearest", false).Length);
        }

        [Test]
        public void AppendedColumns_SpecSection7ThenSidesThenWidth()
        {
            var cols = CsvSchema.AppendedColumns();
            Assert.AreEqual(20 + 10 + 10 + 1, cols.Length);
            CollectionAssert.AreEqual(new[]
            {
                "infer_ms", "infer_hz", "align_s", "align_t", "align_residual", "floor_inliers",
                "walls_in_map", "aimed_source_chain", "edge_snap_frac", "floor_h_m", "thermal_state", "detect_latency_ms",
                "prep_ms",
                // Field-debugging diagnostics: where a frame silently stopped (scheduler, backend, pipeline).
                "sched_status", "infer_status", "frames_dropped",
                // Tracking sanity: how often ARCore's pose or floor jumped.
                "pose_jumps", "floor_jumps",
                // Map integrity: clears on lost tracking, walls removed because the camera saw through them.
                "map_clears", "walls_carved",
            }, CsvSchema.LearnedColumns);
            Assert.AreEqual("left_valid", cols[20]);
            Assert.AreEqual("right_reason", cols[39]);
            Assert.AreEqual("corridor_width_m", cols[40]);
            Assert.AreEqual("floor-aligned-v1", CsvSchema.Revision);
        }
    }
}
