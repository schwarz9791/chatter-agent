using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="Finite"/>。<c>float.IsNaN</c> だけでは通ってしまう ±Infinity も
    /// 非有限として拾うことを固定する（#103）。
    /// </summary>
    [TestFixture]
    public sealed class FiniteTests
    {
        [Test]
        public void OrdinaryVectorIsFinite()
        {
            Assert.That(Finite.IsFinite(new Vector3(1f, -2f, 0f)), Is.True);
        }

        [Test]
        public void NaNVectorIsNotFinite()
        {
            Assert.That(Finite.IsFinite(new Vector3(float.NaN, 0f, 0f)), Is.False);
        }

        [Test]
        public void PositiveInfinityVectorIsNotFinite()
        {
            Assert.That(Finite.IsFinite(new Vector3(0f, float.PositiveInfinity, 0f)), Is.False);
        }

        [Test]
        public void NegativeInfinityVectorIsNotFinite()
        {
            Assert.That(Finite.IsFinite(new Vector3(0f, 0f, float.NegativeInfinity)), Is.False);
        }
    }
}
