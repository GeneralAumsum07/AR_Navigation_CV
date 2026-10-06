using System;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>One prepared network input. The backend writes its result into <see cref="target"/>.</summary>
    public struct InferenceRequest
    {
        /// <summary>size×size×3 RGB, row 0 = top, display-upright and letterboxed (InferenceGeometry).</summary>
        public byte[] rgb;
        public int size;
        /// <summary>Carries the camera for this exact image; returned by TryCollect once filled.</summary>
        public InverseDepthImage target;
    }

    /// <summary>
    /// The ML depth backend, kept behind an interface so LiteRT can replace QNN (spec §4)
    /// without touching the scheduler or the pipeline. At most one request is ever in flight.
    /// </summary>
    public interface IDepthInference : IDisposable
    {
        bool IsAvailable { get; }
        bool IsBusy { get; }
        /// <summary>Human-readable state, logged in the CSV header and shown on the HUD when unavailable.</summary>
        string Status { get; }
        /// <summary>Queue one inference. False when busy or unavailable; the caller drops the frame.</summary>
        bool TryBegin(in InferenceRequest request);
        /// <summary>True once, when the request started by TryBegin has finished.</summary>
        bool TryCollect(out InverseDepthImage image);
    }

    /// <summary>Backend for devices or builds without ML depth; keeps the ARCore path running.</summary>
    public sealed class NullDepthInference : IDepthInference
    {
        public NullDepthInference(string reason) { Status = reason; }
        public bool IsAvailable => false;
        public bool IsBusy => false;
        public string Status { get; }
        public bool TryBegin(in InferenceRequest request) => false;
        public bool TryCollect(out InverseDepthImage image) { image = null; return false; }
        public void Dispose() { }
    }
}
