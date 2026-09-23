using PMNet.Mover;
using PMNet.Projectile;

namespace PMNet.Combat
{
    public enum PMCombatRejectReason : int
    {
        None = 0, NotStarted, MatchEnded, UnknownPlayer, Dead, Disconnected,
        InvalidId, StaleId, InvalidAim, InvalidHero, UnsupportedAttack,
        InvalidConfig, InsufficientMana, InsufficientEnergy, Cooldown,
        Capacity, Expired, UnknownAttack, ProjectileLimit, DirectionMismatch,
        IdentityMismatch, InvalidClock
    }
    public enum PMCombatEndReason : byte { None, FirstKill, Forfeit, Draw }

    public sealed class PMCombatPlayerSnapshot
    {
        public uint Epoch;
        public uint NetId;
        public int Uid;
        public int TeamId;
        public int HeroId;
        public int Hp;
        public int MaxHp;
        public int Mana;
        public int SuperEnergy;
        public bool Dead;
        public bool Connected;
        public PMCombatPlayerSnapshot Clone() { return (PMCombatPlayerSnapshot)MemberwiseClone(); }
    }

    public sealed class PMCombatAttackPlan
    {
        public int HeroId;
        public bool IsSuper;
        public int Damage;
        public int ManaCost;
        public int FireIntervalMs;
        public PMProjectileSpec Spec;
        public PMVector3[] Directions = new PMVector3[0];
        public PMCombatAttackPlan Clone()
        {
            var copy = (PMCombatAttackPlan)MemberwiseClone();
            copy.Spec = Spec == null ? null : Spec.Clone();
            copy.Directions = Directions == null ? null : (PMVector3[])Directions.Clone();
            return copy;
        }
    }

    public sealed class PMCombatAttackDecision
    {
        public uint ActivationId;
        public bool Accepted;
        public bool Duplicate;
        public PMCombatRejectReason Reason;
        public PMCombatAttackPlan Plan;
        public PMCombatAttackDecision Clone()
        {
            var copy = (PMCombatAttackDecision)MemberwiseClone();
            copy.Plan = Plan == null ? null : Plan.Clone();
            return copy;
        }
    }

    public struct PMCombatMatchOutcome
    {
        public bool Ended;
        public uint OutcomeId;
        public int WinnerTeamId;
        public PMCombatEndReason Reason;
    }

    public static class PMCombatLimits
    {
        public const int MaxPlayers = 6;
        public const int MaxTeams = 2;
        public const int MaxAttacks = 512;
        public const int MaxBulletsPerAttack = 64;
        public const int MaxAuthorizedProjectiles = 2048;
        public const int AttackRecordTtlMs = 5000;
        public const int MinFireIntervalMs = 100;
        public const int ResultGraceMs = 5000;
        public const int ResultResendMs = 1000;
        public const float ProjectileRadiusM = 0.1f;
        public const float DirectionToleranceDegrees = 1f;
    }
}
