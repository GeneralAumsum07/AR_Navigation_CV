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
            Assert.AreEqual(12 + 10 + 10 + 1, cols.Length);
            CollectionAssert.AreEqual(new[]
            {
                "infer_ms", "infer_hz", "align_s", "align_t", "align_residual", "floor_inliers",
                "walls_in_map", "aimed_source_chain", "edge_snap_frac", "floor_h_m", "thermal_state", "detect_latency_ms",
            }, CsvSchema.LearnedColumns);
            Assert.AreEqual("left_valid", cols[12]);
            Assert.AreEqual("right_reason", cols[31]);
            Assert.AreEqual("corridor_width_m", cols[32]);
            Assert.AreEqual("floor-aligned-v1", CsvSchema.Revision);
        }
    }
}
