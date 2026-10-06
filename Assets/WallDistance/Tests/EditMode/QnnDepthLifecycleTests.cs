using System;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine.Networking;

namespace WallDistance.Tests
{
    public class QnnDepthLifecycleTests
    {
        [Test]
        public void Dispose_ReleasesAnOwnedModelCopyEvenIfItsCoroutineNeverResumes()
        {
            var type = Type.GetType("WallDistance.AR.QnnDepthInference, WallDistance.AR");
            Assert.IsNotNull(type);
            // Skip the constructor's Android/MonoBehaviour startup. This exercises ownership
            // with a real Unity request, without a phone or a network request being sent.
            var backend = FormatterServices.GetUninitializedObject(type);
            using (var request = UnityWebRequest.Get("http://127.0.0.1:1/unused"))
            {
                var pointer = typeof(UnityWebRequest).GetField("m_Ptr", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(pointer);
                Assert.AreNotEqual(IntPtr.Zero, pointer.GetValue(request));
                type.GetField("_modelRequest", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(backend, request);
                ((IDisposable)backend).Dispose();
                // The SDK's native handle proves release; clearing our reference alone is not cleanup.
                Assert.AreEqual(IntPtr.Zero, pointer.GetValue(request));
            }
        }
    }
}
