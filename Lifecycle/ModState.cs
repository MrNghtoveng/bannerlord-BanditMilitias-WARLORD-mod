namespace BanditMilitias.Lifecycle
{
    public enum ModState
    {
        Uninitialized,
        Loading,
        Ready,
        Dormant,
        Active,
        Degraded,
        Failed,
        EmergencyStop
    }
}
