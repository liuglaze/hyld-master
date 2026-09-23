namespace PMNet.Projectile
{
    // Temporary end-to-end projectile probe, not the normal/super hero weapon system.
    public static class PMProjectileDiagnosticConfig
    {
        public const int FireIntervalMs = 200;
        public const int StepMs = 16;
        public const int MaxStepsPerPump = 8;
        public const float MuzzleOffsetM = 0.6f;
        public static PMProjectileSpec CreateSpec()
        {
            return new PMProjectileSpec { SpeedMps = 10f, RadiusM = 0.1f, LifetimeMs = 1500,
                DelayDestroyMs = 150, StopOnHit = true, HideOnStop = true };
        }
    }
}
