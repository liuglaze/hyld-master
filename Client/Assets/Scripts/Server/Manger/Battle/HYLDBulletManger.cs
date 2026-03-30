/****************************************************
    Author:            龙之介
    CreatTime:    2022/5/7 15:34:53
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;

namespace Manger
{
	public class HYLDBulletManger : MonoBehaviour
	{
        private sealed class VisualAttackSpec
        {
            public GameObject BulletPrefab;
            public float ShootDistance;
            public float ShootWidth;
            public int BulletCount;
            public int BulletDamage;
            public float LaunchAngle;
            public float Speed;
            public int BulletCountByEachTime;
            public float EachTimeBulletsShootSpace;
            public bool IsParadola;
            public float High;
            public bool IsSuper;
        }

        public bool initFinish;
        private Transform bulletParent;
		private float[] attackTimers;
		private List<shell> AllShells = new List<shell>();
        public int ActiveShellCount
        {
            get
            {
                return AllShells.Count;
            }
        }
		public void InitData()
        {
            initFinish = false;
            bulletParent = CreateBulletParent();
            InitializeAttackTimers();
            initFinish = true;
		}


        private void InitializeAttackTimers()
        {
            attackTimers = new float[HYLDStaticValue.Players.Count];
            ResetAttackTimers();
        }

        private Transform CreateBulletParent()
        {
            GameObject parent = new GameObject("BulletPool");
            parent.transform.position = Vector3.zero;
            return parent.transform;
        }

        public void OnLogicUpdate()
        {
			//遍历所有子弹（飞行更新）
			for (int i = 0; i < AllShells.Count; i++)
			{
					AllShells[i].OnUpdateLogic();
			}
		}



		private void ResetAttackTimers()
		{
			if (attackTimers == null)
			{
				return;
			}

			for (int i = 0; i < attackTimers.Length; i++)
			{
				attackTimers[i] = 0f;
			}
		}

		private void RemoveShell(shell shell)
		{
			AllShells.Remove(shell);
		}
		private void AddShell(shell shell, float speed, float health)
		{
			// ★ 联网模式：标记为纯视觉子弹
			shell.isVisualOnly = HYLDStaticValue.isNet;
			shell.InitData(speed, health, RemoveShell);
			AllShells.Add(shell);
		}
        

        /// <summary>
        /// ★ 由权威帧处理直接调用，为指定玩家生成一颗视觉子弹。
        /// 不依赖 fireState 间接触发，解决和解路径跳过 bulletManger.OnLogicUpdate 的问题。
        /// </summary>
        /// <param name="playerID">玩家在 HYLDStaticValue.Players 中的索引</param>
        /// <param name="spawnPosition">子弹出生位置（从权威快照取，而非当前渲染位置）</param>
        /// <param name="fireTowards">射击方向（已经过镜像处理）</param>
        /// <param name="fireState">射击类型</param>
        public void SpawnVisualBullet(int playerID, Vector3 spawnPosition, Vector3 fireTowards, FireState fireState)
        {
            if (!CanSpawnVisualBullet(playerID))
                return;

            SpawnVisualBulletDirect(playerID, spawnPosition, fireTowards, fireState);
        }

        private bool CanSpawnVisualBullet(int playerID)
        {
            return playerID >= 0
                && playerID < HYLDStaticValue.Players.Count
                && HYLDStaticValue.Players[playerID].isNotDie;
        }

        private void SpawnVisualBulletDirect(int playerID, Vector3 spawnPosition, Vector3 fireTowards, FireState fireState)
        {
            HYLDStaticValue.Players[playerID].isCanCure = false;
            attackTimers[playerID] = 0;
            HYLDStaticValue.Players[playerID].bodyAnimator.SetTrigger("Fire");

            Vector3 fireTowardsTemp = fireTowards;
            fireTowardsTemp.y = 0;
            Hero hero = HYLDStaticValue.Players[playerID].hero;

            if (fireState == FireState.ShotgunSuper && hero.isSuperMovingType)
            {
                HYLDStaticValue.Players[playerID].可以按大招 = false;
                HYLDStaticValue.Players[playerID].当前能量 = 0;
                SpawnMovementSuperEntity(playerID, hero, spawnPosition);
                return;
            }

            VisualAttackSpec spec = BuildVisualAttackSpec(hero, fireState);
            if (spec == null || spec.BulletPrefab == null)
            {
                Logging.HYLDDebug.Trace($"[VisualBullet][Skip] playerID={playerID} fireState={fireState} reason=invalid-spec");
                return;
            }

            StartCoroutine(SpawnVisualProjectiles(playerID, spawnPosition, fireTowardsTemp, spec));
        }

        private VisualAttackSpec BuildVisualAttackSpec(Hero hero, FireState fireState)
        {
            VisualAttackSpec spec = new VisualAttackSpec();
            spec.BulletPrefab = hero.shell;
            spec.ShootDistance = hero.shootDistance;
            spec.ShootWidth = hero.shootWidth;
            spec.BulletCount = hero.bulletCount;
            spec.BulletDamage = hero.bulletDamage;
            spec.LaunchAngle = hero.LaunchAngle;
            spec.Speed = hero.speed;
            spec.BulletCountByEachTime = hero.bulletCountByEachTime;
            spec.EachTimeBulletsShootSpace = hero.EachTimebulletsShootSpace;
            spec.IsParadola = hero.IsParadola;
            spec.High = hero.high;
            spec.IsSuper = false;

            if (fireState == FireState.ShotgunSuper)
            {
                spec.IsSuper = true;
                HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].可以按大招 = false;
                HYLDStaticValue.Players[HYLDStaticValue.playerSelfIDInServer].当前能量 = 0;

                if (hero.大招实体 != null)
                {
                    spec.BulletPrefab = hero.大招实体;
                }

                if (hero.superBullet != null)
                {
                    spec.ShootDistance = hero.superBullet.shootDistance;
                    spec.ShootWidth = hero.superBullet.shootWidth;
                    spec.BulletCount = hero.superBullet.bulletCount;
                    if (hero.superBullet.bulletDamage >= 0) spec.BulletDamage = hero.superBullet.bulletDamage;
                    if (hero.superBullet.LaunchAngle >= 0) spec.LaunchAngle = hero.superBullet.LaunchAngle;
                    if (hero.superBullet.speed >= 0) spec.Speed = hero.superBullet.speed;
                    if (hero.superBullet.bulletCountByEachTime >= 0) spec.BulletCountByEachTime = hero.superBullet.bulletCountByEachTime;
                    if (hero.superBullet.EachTimebulletsShootSpace >= 0) spec.EachTimeBulletsShootSpace = hero.superBullet.EachTimebulletsShootSpace;
                    spec.IsParadola = hero.superBullet.IsParadola;
                    if (hero.superBullet.high >= 0) spec.High = hero.superBullet.high;
                }
            }

            if (spec.BulletCountByEachTime <= 0)
            {
                spec.BulletCountByEachTime = Mathf.Max(1, spec.BulletCount);
            }

            return spec;
        }

        private IEnumerator SpawnVisualProjectiles(int playerID, Vector3 spawnPosition, Vector3 fireTowards, VisualAttackSpec spec)
        {
            if (HYLDStaticValue.Players[playerID].hero.heroName == HeroName.BeiYa && spec.BulletCount != 1)
            {
                yield return StartCoroutine(SpawnBeeSuperPattern(playerID, spawnPosition, fireTowards, spec));
                yield break;
            }

            if (spec.LaunchAngle != 0)
            {
                float angle = spec.LaunchAngle / spec.BulletCountByEachTime;
                yield return StartCoroutine(SpawnFanPattern(playerID, spawnPosition, fireTowards, spec, angle));
                yield break;
            }

            yield return StartCoroutine(SpawnStraightPattern(playerID, spawnPosition, fireTowards, spec));
        }

        private IEnumerator SpawnFanPattern(int playerID, Vector3 spawnPosition, Vector3 fireTowards, VisualAttackSpec spec, float angle)
        {
            int tamp = 0;
            int sum = spec.BulletCount;
            int eachSum = spec.BulletCountByEachTime;

            for (int k = 0; k < spec.BulletCount / spec.BulletCountByEachTime; k++)
            {
                float j = -spec.BulletCountByEachTime / 2f;
                if (tamp == 1) { tamp = 0; j += 0.5f; }
                else { tamp = 1; }

                int sumtamp = UnityEngine.Random.Range(eachSum - 1, eachSum + 1);
                if (spec.BulletCount / spec.BulletCountByEachTime < 2) sumtamp = sum;
                else if (spec.BulletCount <= 15) sumtamp = eachSum;
                else if (sum <= sumtamp) sumtamp = sum;
                else sum -= sumtamp;

                for (int i = 0; i < sumtamp; i++, j += UnityEngine.Random.Range(0.8f, 1.2f))
                {
                    shell shellInstance = CreateVisualShell(playerID, spawnPosition, fireTowards, spec, j * angle, 0f);
                    if (shellInstance != null)
                    {
                        FinalizeVisualShell(shellInstance, playerID, spec);
                    }
                }

                if (spec.EachTimeBulletsShootSpace > 0f)
                {
                    yield return new WaitForSeconds(spec.EachTimeBulletsShootSpace);
                }
                else
                {
                    yield return null;
                }
            }
        }

        private IEnumerator SpawnStraightPattern(int playerID, Vector3 spawnPosition, Vector3 fireTowards, VisualAttackSpec spec)
        {
            if (spec.BulletCount / spec.BulletCountByEachTime == 1)
            {
                if (spec.BulletCount == 1)
                {
                    shell shellInstance = CreateVisualShell(playerID, spawnPosition, fireTowards, spec, 0f, 0f);
                    if (shellInstance != null)
                    {
                        FinalizeVisualShell(shellInstance, playerID, spec);
                    }
                    yield break;
                }

                int i;
                float bulletDis = spec.ShootWidth / spec.BulletCountByEachTime;
                float temp = bulletDis;
                for (i = 0; i < spec.BulletCount / 2; i++)
                {
                    temp -= bulletDis;
                    shell shellInstance = CreateVisualShell(playerID, spawnPosition, fireTowards, spec, 0f, temp);
                    if (shellInstance != null)
                    {
                        FinalizeVisualShell(shellInstance, playerID, spec);
                    }

                    if (spec.EachTimeBulletsShootSpace > 0f)
                    {
                        yield return new WaitForSeconds(spec.EachTimeBulletsShootSpace);
                    }
                    else
                    {
                        yield return null;
                    }
                }

                temp = 0;
                for (; i < spec.BulletCount; i++)
                {
                    temp += bulletDis;
                    shell shellInstance = CreateVisualShell(playerID, spawnPosition, fireTowards, spec, 0f, temp);
                    if (shellInstance != null)
                    {
                        FinalizeVisualShell(shellInstance, playerID, spec);
                    }

                    if (spec.EachTimeBulletsShootSpace > 0f)
                    {
                        yield return new WaitForSeconds(spec.EachTimeBulletsShootSpace);
                    }
                    else
                    {
                        yield return null;
                    }
                }
                yield break;
            }

            float multiBulletDis = spec.ShootWidth / spec.BulletCountByEachTime;
            for (int k = 0; k < spec.BulletCount; k++)
            {
                multiBulletDis *= -1;
                shell shellInstance = CreateVisualShell(playerID, spawnPosition, fireTowards, spec, 0f, multiBulletDis);
                if (shellInstance != null)
                {
                    FinalizeVisualShell(shellInstance, playerID, spec);
                }

                if (spec.EachTimeBulletsShootSpace > 0f)
                {
                    yield return new WaitForSeconds(spec.EachTimeBulletsShootSpace);
                }
                else
                {
                    yield return null;
                }
            }
        }

        private IEnumerator SpawnBeeSuperPattern(int playerID, Vector3 spawnPosition, Vector3 fireTowards, VisualAttackSpec spec)
        {
            int i;
            float bulletDis = spec.ShootWidth / spec.BulletCountByEachTime;
            float temp = bulletDis;
            float shootDistanceTime = 0.35f;
            float rotateSpeed = -0.3f;
            for (i = 0; i < spec.BulletCount / 2; i++)
            {
                temp -= bulletDis;
                shell shellInstance = CreateVisualShell(playerID, spawnPosition, fireTowards, spec, 0f, temp);
                if (shellInstance != null)
                {
                    rolateSelf rotateSelf = shellInstance.GetComponent<rolateSelf>();
                    if (rotateSelf != null)
                    {
                        rotateSelf.speed = rotateSpeed;
                        rotateSelf.蜜蜂大招转弯时间 = shootDistanceTime;
                    }
                    FinalizeVisualShell(shellInstance, playerID, spec);
                }

                shootDistanceTime -= 0.1f;
                rotateSpeed -= 1.7f;
            }

            shootDistanceTime = 0.35f;
            rotateSpeed = 0.3f;
            temp = 0f;
            for (; i < spec.BulletCount; i++)
            {
                temp += bulletDis;
                shell shellInstance = CreateVisualShell(playerID, spawnPosition, fireTowards, spec, 0f, temp);
                if (shellInstance != null)
                {
                    rolateSelf rotateSelf = shellInstance.GetComponent<rolateSelf>();
                    if (rotateSelf != null)
                    {
                        rotateSelf.speed = rotateSpeed;
                        rotateSelf.蜜蜂大招转弯时间 = shootDistanceTime;
                    }
                    FinalizeVisualShell(shellInstance, playerID, spec);
                }

                shootDistanceTime -= 0.1f;
                rotateSpeed += 1.7f;
            }

            yield break;
        }

        private shell CreateVisualShell(int playerID, Vector3 spawnPosition, Vector3 fireTowards, VisualAttackSpec spec, float yawOffset, float lateralOffset)
        {
            if (spec.BulletPrefab == null)
            {
                return null;
            }

            GameObject go = Instantiate(spec.BulletPrefab, spawnPosition, Quaternion.identity, bulletParent);
            go.transform.LookAt(go.transform.position + fireTowards);
            if (Mathf.Abs(yawOffset) > 0.001f)
            {
                go.transform.Rotate(new Vector3(0, yawOffset));
            }
            if (Mathf.Abs(lateralOffset) > 0.001f)
            {
                go.transform.Translate(Vector3.right * lateralOffset);
            }

            shell shellInstance = go.GetComponent<shell>();
            if (shellInstance == null)
            {
                Destroy(go);
                return null;
            }

            shellInstance.bulletOnwerID = playerID;
            return shellInstance;
        }

        private void FinalizeVisualShell(shell shellInstance, int playerID, VisualAttackSpec spec)
        {
            ApplyBulletLayer(shellInstance.gameObject);
            shellInstance.bulletDamage = spec.BulletDamage;
            shellInstance.bulletOnwerID = playerID;
            ApplyBehaviorFlags(shellInstance, playerID, spec.IsSuper);
            AddShell(shellInstance, spec.Speed, spec.ShootDistance / Mathf.Max(spec.Speed, 0.01f));
        }

        private void ApplyBulletLayer(GameObject go)
        {
            int bulletLayer = LayerMask.NameToLayer("Bullet");
            go.layer = bulletLayer;
            foreach (Transform child in go.GetComponentsInChildren<Transform>())
            {
                child.gameObject.layer = bulletLayer;
            }
        }

        private void ApplyBehaviorFlags(shell shellInstance, int playerID, bool isSuper)
        {
            HeroName hero = HYLDStaticValue.Players[playerID].hero.heroName;

            if (isSuper)
            {
                switch (hero)
                {
                    case HeroName.RuiKe:
                        shellInstance.behavior = BulletBehavior.Penetrate | BulletBehavior.Reflect;
                        break;
                    case HeroName.KeErTe:
                        shellInstance.behavior = BulletBehavior.Penetrate | BulletBehavior.DestroyWall;
                        break;
                    case HeroName.XueLi:
                        shellInstance.behavior = BulletBehavior.Penetrate | BulletBehavior.DestroyWall | BulletBehavior.CrowdControl;
                        shellInstance.ccDuration = 0.6f;
                        shellInstance.ccHeroName = HeroName.XueLi;
                        break;
                    case HeroName.GeEr:
                        shellInstance.behavior = BulletBehavior.Penetrate | BulletBehavior.CrowdControl;
                        shellInstance.ccDuration = 0.5f;
                        shellInstance.ccHeroName = HeroName.GeEr;
                        break;
                    case HeroName.BeiYa:
                        shellInstance.behavior = BulletBehavior.Slow;
                        shellInstance.slowDuration = 3f;
                        break;
                    default:
                        shellInstance.behavior = BulletBehavior.None;
                        break;
                }
                return;
            }

            switch (hero)
            {
                case HeroName.TaLa:
                    shellInstance.behavior = BulletBehavior.Penetrate;
                    break;
                case HeroName.RuiKe:
                    shellInstance.behavior = BulletBehavior.Reflect;
                    break;
                case HeroName.HeiYa:
                    shellInstance.behavior = BulletBehavior.Poison;
                    break;
                case HeroName.BeiYa:
                    shellInstance.behavior = BulletBehavior.BeeCharge;
                    break;
                case HeroName.PanNi:
                case HeroName.SiPaiKe:
                    shellInstance.behavior = BulletBehavior.ExplodeOnHit;
                    break;
                default:
                    shellInstance.behavior = BulletBehavior.None;
                    break;
            }
        }

		private void SpawnMovementSuperEntity(int playerID, Hero hero, Vector3 spawnPosition)
		{
			GameObject go = Instantiate(hero.大招实体, transform.parent);
			go.transform.position = spawnPosition;
			go.GetComponent<移动型大招>().playerid = playerID;
			go.GetComponent<移动型大招>().当前英雄 = hero.heroName;
			Destroy(go, 2);
		}

    }
}