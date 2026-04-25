namespace BanditMilitias.Core
{
    public static class MathUtils
    {
        public static float Clamp(float value, float min, float max)
            => Infrastructure.MathUtils.Clamp(value, min, max);

        public static float Sigmoid(float value)
            => Infrastructure.MathUtils.Sigmoid(value);
    }
}
