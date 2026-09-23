using System;
using PMNet.Mover;

namespace PMNet.Projectile
{
    public enum PMProjectileOrigin : byte { ClientPredicted = 1, ServerDirect = 2 }
    public enum PMActivationResult : byte { Pending, Confirmed, Rejected }
    public enum PMProjectileHistoryResolution : byte { FrameAnchor, Rewind, Current }

    public struct PMProjectileKey : IEquatable<PMProjectileKey>
    {
        public readonly uint Epoch;
        public readonly uint OwnerNetId;
        public readonly uint ProjectileId;
        public readonly PMProjectileOrigin Origin;
        public PMProjectileKey(uint epoch, uint ownerNetId, uint projectileId, PMProjectileOrigin origin)
        { Epoch = epoch; OwnerNetId = ownerNetId; ProjectileId = projectileId; Origin = origin; }
        public bool IsValid { get { return Epoch != 0 && OwnerNetId != 0 && ProjectileId != 0
            && (Origin == PMProjectileOrigin.ClientPredicted || Origin == PMProjectileOrigin.ServerDirect); } }
        public bool Equals(PMProjectileKey other) { return Epoch == other.Epoch && OwnerNetId == other.OwnerNetId
            && ProjectileId == other.ProjectileId && Origin == other.Origin; }
        public override bool Equals(object obj) { return obj is PMProjectileKey && Equals((PMProjectileKey)obj); }
        public override int GetHashCode() { unchecked { return (((int)Epoch * 397 ^ (int)OwnerNetId) * 397 ^ (int)ProjectileId) * 397 ^ (int)Origin; } }
        public static bool operator ==(PMProjectileKey a, PMProjectileKey b) { return a.Equals(b); }
        public static bool operator !=(PMProjectileKey a, PMProjectileKey b) { return !a.Equals(b); }
    }

    public sealed class PMProjectileState
    {
        public PMProjectileKey Key;
        public uint AuthorityNetId;
        public uint ActivationId;
        public PMVector3 SpawnPosition;
        public PMVector3 PreviousPosition;
        public PMVector3 Position;
        public PMVector3 Velocity;
        public float Yaw;
        public double MoveTimeMs;
        public bool Stopped;
        public bool Hidden;
        public bool TakenOver;
        public double StopWallTimeMs;
        public double TimeAfterStoppedMs;
        public double TombstoneUntilMs;
        public uint[] HitTargets = new uint[0];
        public uint[] AllowedTargets = new uint[0];
        public PMProjectileState Clone()
        {
            PMProjectileState copy = (PMProjectileState)MemberwiseClone();
            copy.HitTargets = HitTargets == null ? new uint[0] : (uint[])HitTargets.Clone();
            copy.AllowedTargets = AllowedTargets == null ? new uint[0] : (uint[])AllowedTargets.Clone();
            return copy;
        }
    }

    public sealed class PMProjectileSpec
    {
        public float SpeedMps = 10f;
        public float RadiusM = 0.1f;
        public int LifetimeMs = 3000;
        public int DelayDestroyMs;
        public bool StopOnHit = true;
        public bool HideOnStop = true;
        public bool SkipFlyingTrajectoryValidation;
        public PMProjectileSpec Clone() { return (PMProjectileSpec)MemberwiseClone(); }
    }

    public sealed class PMProjectileSpawnRequest
    {
        public PMProjectileState State = new PMProjectileState();
        public PMProjectileSpec Spec = new PMProjectileSpec();
        public int PredictionMs;
        public PMProjectileSpawnRequest Clone() { return new PMProjectileSpawnRequest {
            State = State == null ? null : State.Clone(), Spec = Spec == null ? null : Spec.Clone(), PredictionMs = PredictionMs }; }
    }

    public struct PMProjectileHitCandidate
    {
        public uint TargetNetId;
        public uint TargetStreamVersion;
        public PMFrameId TargetServerFrame;
        public PMVector3 ImpactPoint;
        public PMVector3 VisualOffset;
    }

    public sealed class PMProjectileHitBatch
    {
        public PMProjectileKey Key;
        public PMVector3 PreviousPosition;
        public PMVector3 HitPosition;
        public int RewindMs;
        public PMProjectileHitCandidate[] Targets = new PMProjectileHitCandidate[0];
        public PMProjectileHitBatch Clone() { return new PMProjectileHitBatch {
            Key = Key, PreviousPosition = PreviousPosition, HitPosition = HitPosition, RewindMs = RewindMs,
            Targets = Targets == null ? null : (PMProjectileHitCandidate[])Targets.Clone() }; }
    }

    public struct PMProjectileTargetSample
    {
        public uint Epoch;
        public uint NetId;
        public uint StreamVersion;
        public PMFrameId ServerFrame;
        public PMFrameId OutputFrame;
        public double TotalSimTimeMs;
        public double WorldTimeMs;
        public PMVector3 Position;
        public float RadiusM;
        public float HalfHeightM;
        public bool Teleported;
        public bool Alive;
    }

    public struct PMProjectileValidatedHit
    {
        public uint TargetNetId;
        public uint TargetStreamVersion;
        public PMVector3 ImpactPoint;
        public PMProjectileHistoryResolution Resolution;
    }

    public interface IPMProjectileTargetHistory
    {
        bool TryResolve(uint epoch, PMProjectileHitCandidate candidate, double worldNowMs,
            int rewindMs, int extraDeferMs, out PMProjectileTargetSample sample,
            out PMProjectileHistoryResolution resolution);
    }

    // The filter receives a sanitized point. Only authority-owned filters may be supplied.
    public interface IPMProjectileHitFilter
    {
        bool Accept(PMProjectileKey projectile, PMProjectileTargetSample target, PMVector3 sanitizedImpact);
    }

    public static class PMProjectileLimits
    {
        public const int MaxOwners = 64;
        public const int MaxProjectiles = 1024;
        public const int MaxPendingSpawnsPerOwner = 128;
        public const int MaxPendingVerifiesPerKey = 5;
        public const int MaxPendingVerifies = 128;
        public const int MaxPendingHitGroups = 128;
        public const int PendingTtlMs = 2000;
        public const int MaxVerifyCalls = 5;
        public const int MaxTargets = 100;
        public const int HistoryCapacity = 512;
        public const int HistoryAgeMs = 1000;
        public const int MaxRewindMs = 500;
        public const int AnchorAgeMs = 600;
        public const int TickBufferMs = 100;
        public const float MaxSegmentM = 20f;
        public const float MaxVisualOffsetM = 20f;
        public const float HitToleranceM = 0.3f;
        public const float MuzzleToleranceM = 10f;
    }
}
