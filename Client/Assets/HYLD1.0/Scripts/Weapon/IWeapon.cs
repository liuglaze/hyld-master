/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/11/7 2：38 
 * Description:  武器类
 * * * * * * * * * * * * * * * * 
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public abstract class IWeapon
{
    protected WeaponBaseAttribute mWeaponBaseAttribute;
    protected GameObject mWeaponGameObject;
    protected ParticleSystem mPariticle;
    protected LineRenderer mLine;
    protected Light mLight;
    protected AudioSource mAudio;
    protected float mEffectDisplayTime = 0;
    protected ICharacter mOwner;
    public ICharacter owner { set { mOwner = value; } }
    public float AttackDis
    {
        get
        {
            return mWeaponBaseAttribute.bulletshootDistance;
        }
    }
    public IWeapon(WeaponBaseAttribute attribute, GameObject Weaponprefab)
    {
        mWeaponBaseAttribute = attribute;
        mWeaponGameObject = Weaponprefab;

        Transform effect = mWeaponGameObject.transform.Find("Effect");
        mPariticle = effect.GetComponent<ParticleSystem>();
        mLine = effect.GetComponent<LineRenderer>();
        mLight = effect.GetComponent<Light>();
        mAudio = effect.GetComponent<AudioSource>();
    }
    public GameObject gameObject { get { return mWeaponGameObject; } }


    public void Update()
    {
        if (mEffectDisplayTime > 0)
        {
            mEffectDisplayTime -= Time.deltaTime;
            if (mEffectDisplayTime <= 0)
            {
                DisableEffect();
            }
        }
    }

    public void Fire()
    {
        //显示枪口特效
        PlayMuzzleEffect();

        //显示子弹轨迹特效
        PlayBulletEffect();

        //设置特效显示时间
        SetEffetDisplayTime();

        //播放声音
        PlaySound();
    }

    protected abstract void SetEffetDisplayTime();

    protected virtual void PlayMuzzleEffect()
    {
        mPariticle.Stop();
        mPariticle.Play();
        mLight.enabled = true;
    }

    protected abstract void PlayBulletEffect();

    protected void DoPlayBulletEffect(float width)
    {
        //mLine.enabled = true;
        //mLine.startWidth = width; mLine.endWidth = width;
        //mLine.SetPosition(0, mWeaponGameObject.transform.position);
        //mLine.SetPosition(1, targetPosition);
    }

    protected abstract void PlaySound();

    protected void DoPlaySound(string clipName)
    {
        AudioClip clip = FactoryManager.ResourcesAssetFactory.LoadAudioClip(clipName);
        mAudio.clip = clip;
        mAudio.Play();
    }

    private void DisableEffect()
    {
        mLine.enabled = false;
        mLight.enabled = false;
    }
}
public class WeaponBaseAttribute
{
    //英雄枪属性
    protected string mName;
    protected string mAssetName;
    protected WeaponType mWeaponType;
    protected float mbulletshootDistance;
    protected float mbulletshootWidth;
    protected int mbulletCount;
    protected int mbulletDamage;
    protected float mbulletLaunchAngle;
    protected float mbulletshootspeed;
    protected int mbulletCountByEachTime;
    protected float mEachTimebulletsShootSpace;
    protected int mReloadSpeed;

    
    public WeaponBaseAttribute(string name, string assetName,WeaponType weaponType,float 每次发射间隔, int _装弹速度,float _shootDistance, float _shootWidth, int _bulletCount, int _bulletDamage, float _LaunchAngle, float _speed, int _bulletCountByEachTime, float _EachTimebulletsShootSpace = 0.1f)
    {

        mName = name;
        mAssetName = assetName;
        mWeaponType = weaponType;
        mbulletshootDistance = _shootDistance;
        mbulletshootWidth = _shootWidth;
        mbulletCount = _bulletCount;
        mbulletDamage = _bulletDamage;
        mbulletLaunchAngle = _LaunchAngle;
        mbulletshootspeed = _speed;
        mbulletCountByEachTime = _bulletCountByEachTime;
        mEachTimebulletsShootSpace = _EachTimebulletsShootSpace;
        mEachTimebulletsShootSpace = 每次发射间隔;
        mReloadSpeed = _装弹速度;
    }
    public string name { get { return mName; } }
    public string assetName { get { return mAssetName; } }

    public float bulletshootDistance
    {
        get
        {
            return mbulletLaunchAngle;
        }
    }
    public WeaponType weaponType { get { return mWeaponType; } }
}

public class WeaponGun : IWeapon
{
    public WeaponGun(WeaponBaseAttribute baseAttr, GameObject gameObject) : base(baseAttr, gameObject) { }
    protected override void PlayBulletEffect()
    {
        DoPlayBulletEffect(0.05f);
    }

    protected override void PlaySound()
    {
        DoPlaySound("GunShot");
    }

    protected override void SetEffetDisplayTime()
    {
        mEffectDisplayTime = 0.2f;
    }
}
public class WeaponRifle : IWeapon
{
    public WeaponRifle(WeaponBaseAttribute baseAttr, GameObject gameObject) : base(baseAttr, gameObject) { }
    protected override void PlayBulletEffect()
    {
        DoPlayBulletEffect(0.1f);
    }

    protected override void PlaySound()
    {
        DoPlaySound("RifleShot");
    }

    protected override void SetEffetDisplayTime()
    {
        mEffectDisplayTime = 0.3f;
    }
}
public class WeaponRocket : IWeapon
{
    public WeaponRocket(WeaponBaseAttribute baseAttr, GameObject gameObject) : base(baseAttr, gameObject) { }
    protected override void PlayBulletEffect()
    {
        DoPlayBulletEffect(0.3f);
    }

    protected override void PlaySound()
    {
        DoPlaySound("RocketShot");
    }

    protected override void SetEffetDisplayTime()
    {
        mEffectDisplayTime = 0.4f;
    }
}
