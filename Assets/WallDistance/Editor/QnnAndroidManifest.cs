using System;
using System.Xml;

namespace WallDistance.EditorTools
{
    /// <summary>
    /// Android 12+ hides vendor native libraries unless the app requests them. QNN's v75
    /// stub links to Qualcomm's CDSP RPC library on the phone; packaging the stub alone
    /// cannot make that vendor dependency visible in the app's linker namespace.
    /// </summary>
    public static class QnnAndroidManifest
    {
        const string Android = "http://schemas.android.com/apk/res/android";

        public static string Patch(string xml)
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(xml);
            var application = doc.SelectSingleNode("/manifest/application") as XmlElement;
            if (application == null) throw new InvalidOperationException("Android manifest has no application element");
            XmlElement library = null;
            foreach (XmlElement candidate in application.GetElementsByTagName("uses-native-library"))
                if (candidate.GetAttribute("name", Android) == "libcdsprpc.so") { library = candidate; break; }
            if (library == null)
            {
                library = doc.CreateElement("uses-native-library");
                application.AppendChild(library);
            }
            SetAndroidAttribute(doc, library, "name", "libcdsprpc.so");
            // Absence of the NPU driver must still permit install and ARCore-only operation.
            // The managed backend already reports native init failures and falls back.
            SetAndroidAttribute(doc, library, "required", "false");
            return doc.OuterXml;
        }

        static void SetAndroidAttribute(XmlDocument doc, XmlElement element, string name, string value)
        {
            var attribute = doc.CreateAttribute("android", name, Android);
            attribute.Value = value;
            element.SetAttributeNode(attribute);
        }
    }
}
