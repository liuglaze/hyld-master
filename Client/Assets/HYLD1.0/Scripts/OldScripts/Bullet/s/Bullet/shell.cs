/*
 * * * * * * * * * * * * * * * *
 * Author:        赵元恺
 * CreatTime:  2020/7/29 21：22
 * Description:  完成一些英雄拖尾特效，和特殊子弹的效果
 * * * * * * * * * * * * * * * *
*/
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 子弹行为标志位（数据驱动），由 BulletLogic.ParadolaShoot 在生成时设置。
/// 替代原来遍布 OnTriggerEnter 的 40+ 处 gameObject.name 字符串比较。
/// </summary>
[System.Flags]
public enum BulletBehavior
{
    None          = 0,
    /// <summary>穿透：碰到目标/SafeBox/Obstacles 不消亡（卡牌、瑞科大招、柯尔特大招、雪莉大招、格尔大招）</summary>
    Penetrate     = 1 << 0,
    /// <summary>反射：碰到 Obstacles/Wall 时速度 X 反转（瑞科普通+大招）</summary>
    Reflect       = 1 << 1,
    /// <summary>破墙：碰到 Obstacles 时销毁障碍物（柯尔特大招、雪莉大招）</summary>
    DestroyWall   = 1 << 2,
    /// <summary>爆裂特效：碰到目标时生成 PanNiZhaLiXiaoGuo（钱袋、仙人掌）</summary>
    ExplodeOnHit  = 1 << 3,
    /// <summary>乌鸦毒：命中敌人后给目标施加中毒</summary>
    Poison        = 1 << 4,
    /// <summary>蜜蜂充能：命中敌人后切换 beeReady + 加伤</summary>
    BeeCharge     = 1 << 5,
    /// <summary>控制(CC)：命中敌人后施加控制效果（雪莉大招、格尔大招）</summary>
    CrowdControl  = 1 << 6,
    /// <summary>减速：命中敌人后施加减速（大蜜蜂大招）</summary>
    Slow          = 1 << 7,
}

/// <summary>
/// 子弹拖尾特效类型（替代 trailerName 字符串比较）。
/// 由预制体上的 shell 组件预设，或在运行时由 BulletLogic 设置。
/// </summary>
public enum TrailerType
{
    None,
    KaPai,          // 卡牌：小粒子随机散布
    GeEr,           // 格尔普通：中等粒子
    GeErDaZhao,     // 格尔大招：大粒子 + 两侧附加
    JingBi,         // 金币：缩放拖尾
    PeiPei,         // 佩佩：后方膨胀 + 伤害递增
    LiAng,          // 里昂：伤害递减（每物理帧 -9）
}

public class shell : MonoBehaviour
{
    public int bulletDamage;
    public int bulletOnwerID = -1;

    /// <summary>
    /// true = 纯视觉子弹，碰撞只播特效不扣血。
    /// false = 旧逻辑（单机模式等仍然本地判定伤害）。
    /// </summary>
    public bool isVisualOnly = false;

    // ── 数据驱动行为标志（替代 gameObject.name 硬编码） ──
    /// <summary>子弹行为标志位组合，由 BulletLogic.ParadolaShoot 设置</summary>
    public BulletBehavior behavior = BulletBehavior.None;
    /// <summary>CC 控制时间（秒），仅 CrowdControl 标志启用时有效</summary>
    public float ccDuration = 0f;
    /// <summary>CC 控制英雄类型（用于移动型大招组件），仅 CrowdControl 标志启用时有效</summary>
    public HeroName ccHeroName;
    /// <summary>减速持续时间（秒），仅 Slow 标志启用时有效</summary>
    public float slowDuration = 0f;

    //这个是子弹内部的代码
    /// <summary>拖尾特效类型（原 trailerName 字符串，现为枚举）</summary>
    public TrailerType trailerType = TrailerType.None;
    /// <summary>[已弃用] 保留兼容旧预制体序列化，运行时不使用</summary>
    public string trailerName = "NULL";
    private float trailerTimer = 0;
    public GameObject trailer=null;
    private float temp = 0;
    public GameObject PanNiZhaLiXiaoGuo;
    public bool isZhaLie = false;
    private Vector3 m_preVelocity = Vector3.zero;

    private int playerid=-1;
    private float speed;
    private float health;
    private float health_timer_start;
    private float health_timer_cur;
    private Action<shell> diecallBack;
    private Vector3 Net_pos;
    private Vector3 moveDir;
    private bool hasDied = false;

    /// <summary>
    /// 视觉子弹的最大存活时间（秒）
    /// </summary>
    private const float VisualBulletLifetime = 3f;

    public void InitData(float _speed,float _health,Action<shell> diecallBack)
    {
        speed = _speed;
        health = _health;
        health_timer_start = Time.time;
        this.diecallBack = diecallBack;

        // ── 兼容旧预制体：trailerName 字符串 → trailerType 枚举 ──
        if (trailerType == TrailerType.None && trailerName != "NULL")
        {
            switch (trailerName)
            {
                case "KaPai":       trailerType = TrailerType.KaPai; break;
                case "GeEr":        trailerType = TrailerType.GeEr; break;
                case "GeErDaZhao":  trailerType = TrailerType.GeErDaZhao; break;
                case "JingBi":      trailerType = TrailerType.JingBi; break;
                case "PeiPei":      trailerType = TrailerType.PeiPei; break;
                case "LiAng":       trailerType = TrailerType.LiAng; break;
            }
        }

        if (trailerType == TrailerType.PeiPei) temp = 0.3f;
        Net_pos = transform.position;

        moveDir = transform.forward;

        // ★ 视觉子弹：直接禁用所有碰撞体，彻底避免误碰
        if (isVisualOnly)
        {
            Collider[] cols = GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++)
            {
                cols[i].enabled = false;
            }
            // 同时关掉 Rigidbody 物理模拟（只靠 Net_pos 驱动位置）
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
            }
        }

        Renderer rend = GetComponentInChildren<Renderer>();
    }

    public bool IsParadola = false;
    private void FixedUpdate()
    {
        // 直接跟踪逻辑位置，不再使用慢插值
        transform.position = Net_pos;
        if (trailerType == TrailerType.LiAng)
        {
            bulletDamage -= 9;
        }
        //更新拖尾
        if (trailer != null)
        {
            trailerTimer += Time.deltaTime;
            if (trailerTimer > 0.15f)
            {
                ShowTrailer();
                trailerTimer = 0;
            }
        }
        health_timer_cur = Time.time;
    }
    //
    public void OnUpdateLogic()
    {
        float age = health_timer_cur - health_timer_start;

        // 视觉子弹：超过 3 秒强制销毁
        if (isVisualOnly)
        {
            if (age >= VisualBulletLifetime)
            {
                NetGlobal.Instance.AddAction(Die);
                return;
            }
            Net_pos += moveDir * speed * Server.NetConfigValue.frameTime;
            return;
        }

        // 非视觉子弹：使用 health 作为寿命
        if (age >= health)
        {
            NetGlobal.Instance.AddAction(Die);
            return;
        }
        Net_pos += moveDir * speed * Server.NetConfigValue.frameTime;
    }
    // ════════════════════════════════════════════════════════════════
    //  OnTriggerEnter — 碰撞入口（已重构为子方法 + BulletBehavior 标志位）
    // ════════════════════════════════════════════════════════════════
    private void OnTriggerEnter(Collider other)
    {
        // ★ 忽略其他子弹和未标记物体（武器模型等）
        if (other.GetComponent<shell>() != null) return;
        if (other.tag == "Untagged") return;

        // ★ 视觉子弹：只播特效+销毁，不做伤害判定
        if (isVisualOnly)
        {
            OnTrigger_VisualOnly(other);
            return;
        }

        // ↓ 单机模式碰撞逻辑（待全面剥离，已用 BulletBehavior 标志位替代 gameObject.name） ↓
        if (other.tag == "RedSafeBox" || other.tag == "BlueSafeBox")
        {
            OnTrigger_SafeBox(other);
        }
        else if (other.tag == "Obstacles")
        {
            OnTrigger_Obstacles(other);
        }
        else if (other.tag == "Wall")
        {
            OnTrigger_Wall(other);
        }
        else if (other.tag == "Player")
        {
            OnTrigger_Player(other);
        }
        else if (other.tag == "Text")
        {
            OnTrigger_Text(other);
        }
    }

    // ── 便捷方法：检查是否具有指定行为标志 ──
    private bool Has(BulletBehavior flag) { return (behavior & flag) != 0; }

    // ── 视觉子弹碰撞（联网模式，纯表现层） ──
    private void OnTrigger_VisualOnly(Collider other)
    {
        // 跳过发射者自己和队友
        if (other.tag == "Player")
        {
            PlayerLogic pl = other.transform.parent != null
                ? other.transform.parent.GetComponent<PlayerLogic>()
                : null;
            if (pl != null)
            {
                int targetId = pl.playerID;
                if (targetId == bulletOnwerID ||
                    HYLDStaticValue.Players[targetId].teamID == HYLDStaticValue.Players[bulletOnwerID].teamID)
                {
                    return;
                }
            }
        }

        if (other.tag == "Player" || other.tag == "Obstacles" || other.tag == "Wall"
            || other.tag == "RedSafeBox" || other.tag == "BlueSafeBox")
        {
            Die();
        }
    }

    // ── 金库碰撞（单机模式） ──
    private void OnTrigger_SafeBox(Collider other)
    {
        if (other.tag == "RedSafeBox")
        {
            HYLDStaticValue.RedBP -= bulletDamage;
        }
        else
        {
            HYLDStaticValue.BlueBP -= bulletDamage;
        }
        HYLDStaticValue.ConfirmWinOrNot = true;

        // 穿透型子弹不消亡
        if (!isZhaLie && !Has(BulletBehavior.Penetrate))
            Die();

        // 爆裂特效
        if (Has(BulletBehavior.ExplodeOnHit) && PanNiZhaLiXiaoGuo != null)
            Instantiate(PanNiZhaLiXiaoGuo, transform.position, transform.rotation);
    }

    // ── 障碍物碰撞（单机模式） ──
    private void OnTrigger_Obstacles(Collider other)
    {
        if (Has(BulletBehavior.Reflect))
        {
            // 反射：速度 X 反转
            Rigidbody rb = gameObject.GetComponent<Rigidbody>();
            Vector3 v = rb.velocity;
            v.x *= -1;
            rb.velocity = v;
        }
        else if (Has(BulletBehavior.DestroyWall))
        {
            // 破墙：销毁障碍物
            Destroy(other.gameObject);
        }
        else if (Has(BulletBehavior.Penetrate))
        {
            // 穿透型（如格尔大招）：不消亡也不反射
        }
        else
        {
            Die();
        }
    }

    // ── 墙壁碰撞（单机模式） ──
    private void OnTrigger_Wall(Collider other)
    {
        if (Has(BulletBehavior.Reflect))
        {
            Rigidbody rb = gameObject.GetComponent<Rigidbody>();
            Vector3 v = rb.velocity;
            v.x *= -1;
            rb.velocity = v;
        }
        else
        {
            Die();
        }
    }

    // ── 玩家碰撞（单机模式） ──
    private void OnTrigger_Player(Collider other)
    {
        int targetPlayerId = other.transform.parent.GetComponent<PlayerLogic>().playerID;

        if (HYLDStaticValue.Players[targetPlayerId].teamID == HYLDStaticValue.Players[bulletOnwerID].teamID)
            return;

        if (HYLDStaticValue.Players[targetPlayerId].是否有防护罩)
        {
            Die();
            return;
        }

        // ── 特殊效果（标志位驱动） ──
        if (Has(BulletBehavior.Poison))
        {
            HYLDStaticValue.Players[targetPlayerId].body.transform.Find("Canvas").Find("HeiYa").gameObject.SetActive(true);
            HYLDStaticValue.Players[targetPlayerId].isPoisoning = true;
        }
        if (Has(BulletBehavior.BeeCharge))
        {
            ApplyBeeCharge();
        }

        // ── 扣血 + 充能 ──
        HYLDStaticValue.Players[bulletOnwerID].当前能量 += bulletDamage / 2f;
        HYLDStaticValue.Players[targetPlayerId].playerBloodValue -= bulletDamage;

        // ── 穿透判定 ──
        if (!isZhaLie && !Has(BulletBehavior.Penetrate))
            Die();

        // ── 爆裂特效 ──
        if (Has(BulletBehavior.ExplodeOnHit) && PanNiZhaLiXiaoGuo != null)
            Instantiate(PanNiZhaLiXiaoGuo, transform.position, transform.rotation);

        // ── 控制效果(CC) ──
        if (Has(BulletBehavior.CrowdControl))
        {
            var cc = other.GetComponent<移动型大招>();
            if (cc != null)
            {
                cc.playerid = targetPlayerId;
                if (HYLDStaticValue.Players[targetPlayerId].isNotDie)
                {
                    HYLDStaticValue.Players[targetPlayerId].被控制 = true;
                    HYLDStaticValue.Players[targetPlayerId].isNotDie = false;
                    cc.当前英雄 = ccHeroName;
                    cc.控制时间 = ccDuration;
                    // 格尔大招需要子弹引用，雪莉大招需要子弹位置
                    if (ccHeroName == HeroName.GeEr)
                        cc.格尔子弹 = gameObject;
                    else
                        cc.子弹位置 = gameObject.transform.position;
                }
            }
        }

        // ── 减速 ──
        if (Has(BulletBehavior.Slow))
        {
            other.transform.parent.GetComponent<PlayerLogic>().减速(slowDuration);
        }
    }

    // ── Text 目标碰撞（单机模式） ──
    private void OnTrigger_Text(Collider other)
    {
        TextLogic tl = other.gameObject.GetComponent<TextLogic>();
        tl.playerBlood -= bulletDamage;
        tl.playerHurt(bulletDamage);

        // ispanni = 穿透型（不因碰撞消亡）
        tl.ispanni = isZhaLie || Has(BulletBehavior.Penetrate);

        // ── 特殊效果 ──
        if (Has(BulletBehavior.Poison))
        {
            tl.isPoisoning = true;
        }
        if (Has(BulletBehavior.BeeCharge))
        {
            ApplyBeeCharge();
        }
        if (Has(BulletBehavior.ExplodeOnHit) && PanNiZhaLiXiaoGuo != null)
        {
            Die();
            tl.ispanni = true;
            Instantiate(PanNiZhaLiXiaoGuo, transform.position, transform.rotation);
        }
        if (Has(BulletBehavior.CrowdControl))
        {
            tl.ispanni = true;
            if (!tl.被控制)
            {
                tl.被控制 = true;
                tl.控制时间 = ccDuration;
                if (ccHeroName == HeroName.GeEr)
                {
                    tl.当前英雄 = HeroName.GeEr;
                    tl.格尔子弹 = gameObject;
                }
                else
                {
                    tl.子弹位置 = gameObject.transform.position;
                }
            }
        }
        if (Has(BulletBehavior.Slow))
        {
            tl.减速();
            Die();
        }

        if (!tl.ispanni)
            Die();
    }

    // ── 蜜蜂充能共用逻辑（Player/Text 共享） ──
    private void ApplyBeeCharge()
    {
        if (HYLDStaticValue.Players[bulletOnwerID].beeReady)
        {
            HYLDStaticValue.Players[bulletOnwerID].body.transform.Find("Canvas").Find("Bee").gameObject.SetActive(false);
            HYLDStaticValue.Players[bulletOnwerID].beeReady = false;
            bulletDamage += 1200;
        }
        else
        {
            HYLDStaticValue.Players[bulletOnwerID].beeReady = true;
            HYLDStaticValue.Players[bulletOnwerID].body.transform.Find("Canvas").Find("Bee").gameObject.SetActive(true);
        }
    }
    public void ShowTrailer()
    {

        GameObject go = Instantiate(trailer, this.gameObject.transform.position, this.gameObject.transform.rotation);
        float tamp=0;
        if (trailerType == TrailerType.KaPai)
        {
            go.transform.Translate(Vector3.down * UnityEngine.Random.Range(-0.5f, 0.5f));
            go.transform.localScale = new Vector3(UnityEngine.Random.Range(0.05f, 0.17f), UnityEngine.Random.Range(0.05f, 0.17f),UnityEngine.Random.Range(0.05f, 0.17f));

        }

        else if(trailerType == TrailerType.GeEr)
        {

            go.transform.Translate(Vector3.down * UnityEngine.Random.Range(-0.5f, 0.5f));
            go.transform.localScale = new Vector3(UnityEngine.Random.Range(0.1f, 0.3f), UnityEngine.Random.Range(0.1f, 0.3f), UnityEngine.Random.Range(0.1f, 0.3f));

        }
        else if(trailerType == TrailerType.GeErDaZhao)
        {
            go.transform.Translate(Vector3.down * UnityEngine.Random.Range(-0.5f, 0.5f));
            go.transform.localScale = new Vector3(UnityEngine.Random.Range(0.4f, 0.9f), UnityEngine.Random.Range(0.4f, 0.9f), UnityEngine.Random.Range(0.4f, 0.9f));
            GameObject go1 = Instantiate(trailer, this.gameObject.transform.position, this.gameObject.transform.rotation);
            go1.transform.Translate(Vector3.right * UnityEngine.Random.Range(-1f, -0.5f));
            go1.transform.localScale = new Vector3(UnityEngine.Random.Range(0.4f, 0.9f), UnityEngine.Random.Range(0.4f, 0.9f), UnityEngine.Random.Range(0.4f, 0.9f));
            GameObject go2 = Instantiate(trailer, this.gameObject.transform.position, this.gameObject.transform.rotation);
            go2.transform.Translate(Vector3.right * UnityEngine.Random.Range(0.5f, 1f));
            go2.transform.localScale = new Vector3(UnityEngine.Random.Range(0.4f, 0.9f), UnityEngine.Random.Range(0.4f, 0.9f), UnityEngine.Random.Range(0.4f, 0.9f));
            Destroy(go1, 0.2f);
            Destroy(go2, 0.2f);
        }
        else if(trailerType == TrailerType.JingBi)
        {
            go.transform.Translate(Vector3.down * UnityEngine.Random.Range(-0.2f, 0));
            temp = 0.5f;
            go.transform.localScale = new Vector3(1,1,1)*temp;
            tamp += 0.2f;
        }
        else if(trailerType == TrailerType.PeiPei)
        {

            go.transform.Translate(Vector3.back);
            go.transform.localScale = new Vector3(1,1,1)*temp;
            temp += 0.01f;
            bulletDamage += 60;
        }

        if(trailerType != TrailerType.PeiPei)
        Destroy(go, 0.2f+tamp);
    }
    public virtual void ShowSkill()
    {

    }
    private void Die()
    {
        if (hasDied)
        {
            return;
        }

        hasDied = true;
        diecallBack?.Invoke(this);
        //到达死亡时间
        Destroy(gameObject);
    }
}


