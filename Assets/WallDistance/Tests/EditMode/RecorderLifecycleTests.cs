using System;
using System.Reflection;
using NUnit.Framework;

namespace WallDistance.Tests
{
    /// <summary>
    /// Unity invokes a MonoBehaviour method named Start automatically, whatever its visibility.
    /// The recorder once exposed its "begin recording" call as public Start(), so every launch
    /// silently started a CSV and the HUD's Record button actually stopped it. EditMode cannot
    /// run the player loop, so this pins the cause instead: no lifecycle-named recording method.
    /// </summary>
    public class RecorderLifecycleTests
    {
        [Test]
        public void Recorder_HasNoUnityStartMessage()
        {
            // Looked up by name: this assembly deliberately references only WallDistance.Core.
            var recorder = Type.GetType("WallDistance.AR.MeasurementCsvRecorder, WallDistance.AR");
            Assert.IsNotNull(recorder, "recorder type not found; was it renamed or moved?");
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Assert.IsNull(recorder.GetMethod("Start", all),
                "a method named Start runs on launch and would begin recording unasked");
        }
    }
}
