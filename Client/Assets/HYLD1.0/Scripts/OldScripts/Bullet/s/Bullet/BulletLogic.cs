/*
 * * * * * * * * * * * * * * * * 
 * Author:        魏佳楠
 * CreatTime:  2020/6/19 17:49:38 
 * Description: 子弹自己的逻辑
 * * * * * * * * * * * * * * * * 
*/

using System;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
public class BulletLogic : MonoBehaviour
{
    public void setBulletInformation(Hero hero)
    {
        shootDistance = hero.shootDistance;
        shootWidth = hero.shootWidth;
        bulletCount = hero.bulletCount;
        bulletDamage = hero.bulletDamage;
        LaunchAngle = hero.LaunchAngle;
        speed = hero.speed;
        bulletCountByEachTime = hero.bulletCountByEachTime;
        EachTimebulletsShootSpace = hero.EachTimebulletsShootSpace;
        bulletPrefab = hero.shell;
        BoomPrefab = hero.Boom;
        high = hero.high;
        IsParadola = hero.IsParadola;
    }

    /// <summary>
    /// 用大招参数覆写当前子弹信息。-1 值表示保留 setBulletInformation 写入的普通攻击值。
    /// 必须在 setBulletInformation 之后调用。
    /// </summary>
    public void applySuperParams(SuperBulletParams sp)
    {
        if (sp == null) return;
        shootDistance = sp.shootDistance;
        shootWidth = sp.shootWidth;
        bulletCount = sp.bulletCount;
        if (sp.bulletDamage >= 0)           bulletDamage = sp.bulletDamage;
        if (sp.LaunchAngle >= 0)            LaunchAngle = sp.LaunchAngle;
        if (sp.speed >= 0)                  speed = sp.speed;
        if (sp.bulletCountByEachTime >= 0)  bulletCountByEachTime = sp.bulletCountByEachTime;
        if (sp.EachTimebulletsShootSpace >= 0) EachTimebulletsShootSpace = sp.EachTimebulletsShootSpace;
        IsParadola = sp.IsParadola;
        if (sp.high >= 0)                   high = sp.high;
        if (sp.Boom != null)                BoomPrefab = sp.Boom;
    }


    // Start is called before the first frame update
    public float shootDistance;//射程
    public float shootWidth;//射击宽度（直线）
    public int bulletCount;//子弹总数量
    public int bulletDamage;//伤害
    public float LaunchAngle = 0;//扇形角度
    public float speed = 10f;//飞行速度
    public int bulletCountByEachTime;//每行子弹的数量
    public GameObject bulletPrefab;//子弹预制体
    private Transform firePos;//开火点
    public float EachTimebulletsShootSpace;//每次开火的时间戳
                                           //zyk的代码
    public int bulletOnwerID = -1;
    public GameObject bulletBody;
    public Vector3 Towards;
    public int bulletHurt;
    //dlh
    public bool IsParadola = false;//是否抛物线射击
    public GameObject BoomPrefab;//爆炸预制体
    public float high;//抛物线高度
    private Action<shell,float,float> CallBack;
    /// <summary>true = 当前发射的是大招子弹。由 HYLDBulletManger.Attack() 在 ShotgunSuper 分支设置。</summary>
    public bool isSuper = false;
    //jn的代码
    public void InitData(Action<shell, float, float> CallBack)
    {
        firePos = bulletBody.transform;
        if (bulletOnwerID == 0)
        {
            gameObject.GetComponent<AudioSource>().volume = 1;
        }
        Invoke("Shoot", 0);
        this.CallBack = CallBack;
        Destroy(this.gameObject, 5);
    }


    void Shoot()
    {
        if(HYLDStaticValue.Players[bulletOnwerID].hero.heroName == HeroName.BeiYa && bulletCount!=1)
        {
            蜜蜂大招();
        }
        //Logging.HYLDDebug.LogError(bulletPrefab);
        //Logging.HYLDDebug.LogError("bulletLogic" + bulletOnwerID+gameObject.name);
        else if (LaunchAngle != 0)//扇形的
        {
            float angle = LaunchAngle / bulletCountByEachTime;
            StartCoroutine(ShanxingShoot(angle));
        }
        else if (LaunchAngle == 0)//直线型的
        {
            StartCoroutine(StraightShoot());
        }
    }
    IEnumerator ShanxingShoot(float angle)//扇形发射的
    {
        int tamp = 0;
        int sum = bulletCount;
        int eachSum = bulletCountByEachTime;
        int sumtamp;
        for (int k = 0; k < bulletCount / bulletCountByEachTime; k++)
        {
            float j = -bulletCountByEachTime / 2;
            if (tamp == 1) { tamp = 0; j += 0.5f; }
            else { tamp = 1; }
            sumtamp = UnityEngine.Random.Range(eachSum - 1, eachSum + 1);
            if (bulletCount / bulletCountByEachTime < 2) sumtamp = sum;//卡牌大师这种单发一排的
            else if (bulletCount <= 15) sumtamp = eachSum;//子弹稀疏的且子弹不多的
            else if (sum <= sumtamp) sumtamp = sum;//子弹过多的散弹枪
            else sum -= sumtamp;
            for (int i = 0; i < sumtamp; i++, j += UnityEngine.Random.Range(0.8f, 1.2f))
            {
                //Logging.HYLDDebug.LogError(Towards);
                GameObject go = GameObject.Instantiate(bulletPrefab, HYLDStaticValue.Players[bulletOnwerID].playerPositon, Quaternion.Euler(Towards)) as GameObject;
                go.transform.LookAt(go.transform.position + Towards);

                go.transform.Rotate(new Vector3(0, j * angle));
                ParadolaShoot(go);
                //Logging.HYLDDebug.LogError(go);
            }
            yield return new WaitForSeconds(EachTimebulletsShootSpace);
        }

    }
    IEnumerator StraightShoot()//直线型
    {
        if (bulletCount /bulletCountByEachTime ==1)//单发子弹的
        {
            if(bulletCount==1)
            {
                GameObject go = GameObject.Instantiate(bulletPrefab, HYLDStaticValue.Players[bulletOnwerID].playerPositon, Quaternion.Euler(Towards)) as GameObject;
                Logging.HYLDDebug.LogError($"[BULLET_DIAG] StraightShoot single go={go.name} pos={go.transform.position} towards={Towards} bulletPrefab={bulletPrefab.name} ownerPos={HYLDStaticValue.Players[bulletOnwerID].playerPositon}");
                go.transform.LookAt(go.transform.position + Towards);
                go.GetComponent<shell>().bulletOnwerID = bulletOnwerID;
                ParadolaShoot(go);
             
            }
            else
            {
                int i;
                float bulletDis = shootWidth / bulletCountByEachTime;
                float temp = bulletDis;
                for (i=0;i<bulletCount/2;i++)
                {
                    GameObject go = GameObject.Instantiate(bulletPrefab, HYLDStaticValue.Players[bulletOnwerID].playerPositon, Quaternion.Euler(Towards)) as GameObject;

                    temp -=bulletDis;
                    go.transform.LookAt(go.transform.position + Towards);
                    go.transform.Translate(Vector3.right * temp);
                    go.GetComponent<shell>().bulletOnwerID = bulletOnwerID;
                    ParadolaShoot(go);
          
                   // Logging.HYLDDebug.LogError(temp);
                    yield return new WaitForSeconds(EachTimebulletsShootSpace);
                }
                temp = 0;
                for (; i < bulletCount; i++)
                {
                    GameObject go = GameObject.Instantiate(bulletPrefab, HYLDStaticValue.Players[bulletOnwerID].playerPositon, Quaternion.Euler(Towards)) as GameObject;
                    
                    temp += bulletDis;
                   // Logging.HYLDDebug.LogError(temp);
                    go.transform.LookAt(go.transform.position + Towards);
                    go.transform.Translate(Vector3.right * temp);
                    go.GetComponent<shell>().bulletOnwerID = bulletOnwerID;
                    ParadolaShoot(go);
         
                    
                    yield return new WaitForSeconds(EachTimebulletsShootSpace);
                }
            }
        }
        else//连续发射的
        {
            float bulletDis = shootWidth / bulletCountByEachTime;
            //Logging.HYLDDebug.Log("?");
            //这个需要获取玩家的位置，不然会非常的僵硬特指firePos
            for (int k = 0; k < bulletCount; k++)
            {
                //print( HYLDStaticValue.Players[bulletOnwerID].body.transform.position);
                GameObject go = Instantiate(bulletPrefab, HYLDStaticValue.Players[bulletOnwerID].playerPositon, Quaternion.Euler(Towards)) as GameObject;
                
                bulletDis *= -1;
                go.transform.LookAt(go.transform.position + Towards);
                go.transform.Translate(Vector3.right * bulletDis);
                go.GetComponent<shell>().bulletOnwerID = bulletOnwerID;
                ParadolaShoot(go);
               

                yield return new WaitForSeconds(EachTimebulletsShootSpace);
            }
        }
    }

    public void 蜜蜂大招()
    {
        int i;
        float bulletDis = shootWidth / bulletCountByEachTime;
        float temp = bulletDis;
        float shootdistancetime = 0.35f;
        float rotatespeed = -0.3f;
        for (i = 0; i < bulletCount / 2; i++)
        {
            GameObject go = GameObject.Instantiate(bulletPrefab, HYLDStaticValue.Players[bulletOnwerID].playerPositon, Quaternion.Euler(Towards)) as GameObject;

            temp -= bulletDis;
            go.transform.LookAt(go.transform.position + Towards);
            go.transform.Translate(Vector3.right * temp);
            go.GetComponent<shell>().bulletOnwerID = bulletOnwerID;
            go.GetComponent<rolateSelf>().speed = rotatespeed;
            go.GetComponent<rolateSelf>().蜜蜂大招转弯时间 = shootdistancetime;
            shootdistancetime -= 0.1f;
            rotatespeed -= 1.7f;
            ParadolaShoot(go);
           
            
        }
        shootdistancetime = 0.35f;
        rotatespeed = 0.3f;
        temp = 0;
        for (; i < bulletCount; i++)
        {
            GameObject go = GameObject.Instantiate(bulletPrefab, HYLDStaticValue.Players[bulletOnwerID].playerPositon, Quaternion.Euler(Towards)) as GameObject;

            temp += bulletDis;
            go.transform.LookAt(go.transform.position + Towards);
            go.transform.Translate(Vector3.right * temp);
            go.GetComponent<shell>().bulletOnwerID = bulletOnwerID;
            go.GetComponent<rolateSelf>().speed = rotatespeed;
            go.GetComponent<rolateSelf>().蜜蜂大招转弯时间 = shootdistancetime;
            shootdistancetime -= 0.1f;
            rotatespeed += 1.7f;
            ParadolaShoot(go);
        }
    }

    public void ParadolaShoot(GameObject go)
    {
        // ★ 设置子弹 Layer，通过物理矩阵避免子弹间互碰及碰到发射者
        int bulletLayer = LayerMask.NameToLayer("Bullet");
        go.layer = bulletLayer;
        foreach (Transform child in go.GetComponentsInChildren<Transform>())
        {
            child.gameObject.layer = bulletLayer;
        }

        if (IsParadola)
        {
            //TODO:重置抛物线攻击
            
            GameObject Boom = GameObject.Instantiate(BoomPrefab, HYLDStaticValue.Players[bulletOnwerID].playerPositon + go.transform.forward * shootDistance, Quaternion.Euler(Towards)) as GameObject;
            // GameObject Boom = GameObject.Instantiate(BoomPrefab, go.transform);
            Boom.GetComponent<BoomCreater>().BoomDamage = bulletDamage;
            Boom.GetComponent<BoomCreater>().BoomOnwerID = bulletOnwerID;
            Boom.GetComponent<BoomCreater>().BKTime = shootDistance / speed;
            Boom.GetComponent<BoomCreater>().BoomRange = shootWidth;
            Boom.GetComponent<BoomCreater>().跟随物 = go;
            go.GetComponent<Rigidbody>().useGravity = true;
            go.GetComponent<Rigidbody>().velocity = go.transform.forward * speed + go.transform.up * shootDistance / speed * high / 2;
        }
        else
        {
            //Debug.LogError((go.transform.forward * speed));
           // go.GetComponent<Rigidbody>().velocity = go.transform.forward * speed;
        }
        //Debug.LogError("go:" + go.transform.forward * speed);
        shell shell = go.GetComponent<shell>();
        //Logging.HYLDDebug.LogError(go + "  "+ go.GetComponent<Rigidbody>().velocity);
        shell.bulletDamage = bulletDamage;
        shell.bulletOnwerID = bulletOnwerID;

        // ── 设置数据驱动行为标志（替代 OnTriggerEnter 中 40+ 处 gameObject.name 比较） ──
        SetBehaviorFlags(shell);

        CallBack?.Invoke(shell, speed, shootDistance / speed);
        //Destroy(go, shootDistance / speed);
    }

    /// <summary>
    /// 根据英雄类型和是否大招，设置 shell 的 BulletBehavior 标志位。
    /// 映射关系源自原 OnTriggerEnter 中的 gameObject.name 判断。
    /// </summary>
    private void SetBehaviorFlags(shell s)
    {
        HeroName hero = HYLDStaticValue.Players[bulletOnwerID].hero.heroName;

        if (isSuper)
        {
            // ── 大招子弹行为 ──
            switch (hero)
            {
                case HeroName.RuiKe:
                    // 大招3瑞科: 穿透+反射
                    s.behavior = BulletBehavior.Penetrate | BulletBehavior.Reflect;
                    break;
                case HeroName.KeErTe:
                    // 大招13柯尔特: 穿透+破墙
                    s.behavior = BulletBehavior.Penetrate | BulletBehavior.DestroyWall;
                    break;
                case HeroName.XueLi:
                    // 大招14雪莉: 穿透+破墙+CC(0.6s)
                    s.behavior = BulletBehavior.Penetrate | BulletBehavior.DestroyWall | BulletBehavior.CrowdControl;
                    s.ccDuration = 0.6f;
                    s.ccHeroName = HeroName.XueLi;
                    break;
                case HeroName.GeEr:
                    // 大招11格尔1: 穿透+CC(0.5s)
                    s.behavior = BulletBehavior.Penetrate | BulletBehavior.CrowdControl;
                    s.ccDuration = 0.5f;
                    s.ccHeroName = HeroName.GeEr;
                    break;
                case HeroName.BeiYa:
                    // 大招16小蜜蜂: 减速
                    s.behavior = BulletBehavior.Slow;
                    s.slowDuration = 3f;
                    break;
                default:
                    s.behavior = BulletBehavior.None;
                    break;
            }
        }
        else
        {
            // ── 普通攻击子弹行为 ──
            switch (hero)
            {
                case HeroName.TaLa:
                    // 子弹6卡牌: 穿透
                    s.behavior = BulletBehavior.Penetrate;
                    break;
                case HeroName.RuiKe:
                    // 子弹3瑞科: 反射
                    s.behavior = BulletBehavior.Reflect;
                    break;
                case HeroName.HeiYa:
                    // 子弹8乌鸦: 毒
                    s.behavior = BulletBehavior.Poison;
                    break;
                case HeroName.BeiYa:
                    // 子弹16小蜜蜂: 蜜蜂充能
                    s.behavior = BulletBehavior.BeeCharge;
                    break;
                case HeroName.PanNi:
                    // 子弹12钱袋: 爆裂特效
                    s.behavior = BulletBehavior.ExplodeOnHit;
                    break;
                case HeroName.SiPaiKe:
                    // 子弹17仙人掌: 爆裂特效
                    s.behavior = BulletBehavior.ExplodeOnHit;
                    break;
                default:
                    s.behavior = BulletBehavior.None;
                    break;
            }
        }
    }
}
