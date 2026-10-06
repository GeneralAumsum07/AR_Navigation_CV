using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class InverseDepthImageMaskTests
    {
        [Test]
        public void MaskOutsideContent_SetsLetterboxToNaN_KeepsContent()
        {
            var img = new InverseDepthImage(4, 3) { content = new RectInt(1, 0, 2, 3) };
            for (int i = 0; i < img.values.Length; i++) img.values[i] = 1f;
            img.MaskOutsideContent();
            for (int v = 0; v < 3; v++)
            for (int u = 0; u < 4; u++)
            {
                float x = img.values[v * 4 + u];
                if (u == 1 || u == 2) Assert.AreEqual(1f, x, $"content pixel ({u},{v}) kept");
                else Assert.IsTrue(float.IsNaN(x), $"letterbox pixel ({u},{v}) masked");
            }
        }
    }
}
