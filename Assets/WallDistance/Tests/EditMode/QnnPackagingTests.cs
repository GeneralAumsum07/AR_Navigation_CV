using System;
using System.Reflection;
using System.Xml;
using NUnit.Framework;

namespace WallDistance.Tests
{
    public class QnnPackagingTests
    {
        const string Android = "http://schemas.android.com/apk/res/android";

        static XmlDocument Patch(string xml)
        {
            // Reflection keeps runtime/Core tests independent of the Editor build assembly.
            var type = Type.GetType("WallDistance.EditorTools.QnnAndroidManifest, WallDistance.Editor");
            Assert.IsNotNull(type, "QNN packaging must expose the vendor RPC library to Android 12+ apps");
            var method = type.GetMethod("Patch", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method);
            var result = new XmlDocument();
            result.LoadXml((string)method.Invoke(null, new object[] { xml }));
            return result;
        }

        [Test]
        public void VendorRpc_IsDeclaredOptional_PreservingArCoreFallbackAndExistingMetadata()
        {
            var doc = Patch("<manifest xmlns:android='" + Android + "'><application><meta-data android:name='existing' /></application></manifest>");
            var library = (XmlElement)doc.SelectSingleNode("/manifest/application/uses-native-library");
            Assert.IsNotNull(library);
            Assert.AreEqual("libcdsprpc.so", library.GetAttribute("name", Android));
            Assert.AreEqual("false", library.GetAttribute("required", Android));
            Assert.IsNotNull(doc.SelectSingleNode("/manifest/application/meta-data"));
        }

        [Test]
        public void RepeatedPostprocessing_DoesNotDuplicateDependency_AndMakesItOptional()
        {
            string xml = "<manifest xmlns:android='" + Android + "'><application><uses-native-library android:name='libcdsprpc.so' android:required='true'/></application></manifest>";
            var once = Patch(xml);
            var twice = Patch(once.OuterXml);
            Assert.AreEqual(1, twice.SelectNodes("/manifest/application/uses-native-library").Count);
            Assert.AreEqual("false", ((XmlElement)twice.SelectSingleNode("/manifest/application/uses-native-library")).GetAttribute("required", Android));
        }
    }
}
