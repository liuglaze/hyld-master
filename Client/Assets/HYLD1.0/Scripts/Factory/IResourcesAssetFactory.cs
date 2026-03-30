using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface IResourcesAssetFactory
{
    GameObject LoadSoldier(string name);
    GameObject LoadEnemy(string name);
    GameObject LoadWeapon(string name);
    GameObject LoadEffect(string name);
    AudioClip LoadAudioClip(string name);
    Sprite LoadSprite(string name);
}

public class ResourcesAssetProxyFactory : IResourcesAssetFactory
{
    private ResourcesAssetFactory mAssetFactory = new ResourcesAssetFactory();
    private Dictionary<string, GameObject> mSoldiers = new Dictionary<string, GameObject>();
    private Dictionary<string, GameObject> mEnemys = new Dictionary<string, GameObject>();
    private Dictionary<string, GameObject> mWeapons = new Dictionary<string, GameObject>();
    private Dictionary<string, GameObject> mEffects = new Dictionary<string, GameObject>();
    private Dictionary<string, AudioClip> mAudioClips = new Dictionary<string, AudioClip>();
    private Dictionary<string, Sprite> mSprites = new Dictionary<string, Sprite>();



    public GameObject LoadSoldier(string name)
    {
        if (mSoldiers.ContainsKey(name))
        {
            return GameObject.Instantiate(mSoldiers[name]);
        }
        else
        {
            GameObject asset = mAssetFactory.LoadAsset(ResourcesAssetFactory.SoldierPath + name) as GameObject;
            mSoldiers.Add(name, asset);
            return GameObject.Instantiate(asset);
        }
    }

    public GameObject LoadEnemy(string name)
    {
        if (mEnemys.ContainsKey(name))
        {
            return GameObject.Instantiate(mEnemys[name]);
        }
        else
        {
            GameObject asset = mAssetFactory.LoadAsset(ResourcesAssetFactory.EnemyPath + name) as GameObject;
            mEnemys.Add(name, asset);
            return GameObject.Instantiate(asset);
        }
    }

    public GameObject LoadWeapon(string name)
    {
        if (mWeapons.ContainsKey(name))
        {
            return GameObject.Instantiate(mWeapons[name]);
        }
        else
        {
            GameObject asset = mAssetFactory.LoadAsset(ResourcesAssetFactory.WeaponPath + name) as GameObject;
            mWeapons.Add(name, asset);
            return GameObject.Instantiate(asset);
        }
    }

    public GameObject LoadEffect(string name)
    {
        if (mEffects.ContainsKey(name))
        {
            return GameObject.Instantiate(mEffects[name]);
        }
        else
        {
            GameObject asset = mAssetFactory.LoadAsset(ResourcesAssetFactory.EffectPath + name) as GameObject;
            mEffects.Add(name, asset);
            return GameObject.Instantiate(asset);
        }
    }

    public AudioClip LoadAudioClip(string name)
    {
        if (mAudioClips.ContainsKey(name))
        {
            return mAudioClips[name];
        }
        else
        {
            AudioClip audio = mAssetFactory.LoadAudioClip(name);
            mAudioClips.Add(name, audio);
            return audio;
        }
    }

    public Sprite LoadSprite(string name)
    {
        if (mSprites.ContainsKey(name))
        {
            return mSprites[name];
        }
        else
        {
            Sprite sprite = mAssetFactory.LoadSprite(name);
            mSprites.Add(name, sprite);
            return sprite;
        }
    }
}

public class ResourcesAssetFactory : IResourcesAssetFactory
{
    public const string SoldierPath = "Characters/";
    public const string EnemyPath = "Characters/";
    public const string WeaponPath = "Weapons/";
    public const string EffectPath = "Effects/";
    public const string AudioPath = "Audios/";
    public const string SpritePath = "Sprites/";

    public GameObject LoadSoldier(string name)
    {
        return InstantiateGameObject(SoldierPath + name);
    }

    public GameObject LoadEnemy(string name)
    {
        return InstantiateGameObject(EnemyPath + name);
    }

    public GameObject LoadWeapon(string name)
    {
        return InstantiateGameObject(WeaponPath + name);
    }

    public GameObject LoadEffect(string name)
    {
        return InstantiateGameObject(EffectPath + name);
    }

    public AudioClip LoadAudioClip(string name)
    {
        return Resources.Load(SpritePath + name, typeof(AudioClip)) as AudioClip;
    }

    public Sprite LoadSprite(string name)
    {
        return Resources.Load(SpritePath + name, typeof(Sprite)) as Sprite;
    }

    private GameObject InstantiateGameObject(string path)
    {
        UnityEngine.Object o = Resources.Load(path);
        if (o == null)
        {
            Logging.HYLDDebug.LogError("无法加载资源，路径:" + path); return null;
        }
        return UnityEngine.GameObject.Instantiate(o) as GameObject;
    }
    public UnityEngine.Object LoadAsset(string path)
    {
        UnityEngine.Object o = Resources.Load(path);
        if (o == null)
        {
            Logging.HYLDDebug.LogError("无法加载资源，路径:" + path); return null;
        }
        return o;
    }

}
