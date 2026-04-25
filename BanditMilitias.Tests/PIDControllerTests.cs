using BanditMilitias.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class PIDControllerTests
    {
        [TestMethod]
        public void Update_FirstSample_DoesNotApplyDerivativeKick()
        {
            var controller = new PIDController(kp: 0f, ki: 0f, kd: 1f, setpoint: 10f);

            controller.Update(currentValue: 0f, deltaTime: 1f);

            Assert.AreEqual(0f, controller.Output, 0.0001f);
        }

        [TestMethod]
        public void Update_ReversedError_RecoversWithoutIntegralWindup()
        {
            var controller = new PIDController(kp: 0f, ki: 1f, kd: 0f, setpoint: 10f);

            for (int i = 0; i < 10; i++)
            {
                controller.Update(currentValue: 0f, deltaTime: 1f);
            }

            controller.Setpoint = 0f;
            controller.Update(currentValue: 10f, deltaTime: 1f);

            Assert.IsTrue(controller.Output < 0f, "Output should turn negative once the error reverses.");
        }

        [TestMethod]
        public void Reset_ClearsControllerState()
        {
            var controller = new PIDController(kp: 0f, ki: 1f, kd: 0f, setpoint: 10f);

            controller.Update(currentValue: 0f, deltaTime: 1f);
            controller.Reset();
            controller.Setpoint = 0f;
            controller.Update(currentValue: 0f, deltaTime: 1f);

            Assert.AreEqual(0f, controller.Output, 0.0001f);
        }
    }
}