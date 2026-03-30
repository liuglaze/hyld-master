using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface IAttributeFactory
{
    CharacterBaseAttribute GetCharacterBaseAttr(HeroName hero);
    WeaponBaseAttribute GetWeaponBaseAttr(HeroName hero);
}
public class AttributeFactory : IAttributeFactory
{
    private Dictionary<HeroName, CharacterBaseAttribute> mCharacterBaseAttrDict;
    private Dictionary<HeroName, WeaponBaseAttribute> mWeaponBaseAttrDict;


    public AttributeFactory()
    {
        InitCharacterBaseAttr();
        InitWeaponBaseAttr();
    }

    private void InitCharacterBaseAttr()
    {
        mCharacterBaseAttrDict = new Dictionary<HeroName, CharacterBaseAttribute>
        {
            { HeroName.DaLiEr, new CharacterBaseAttribute("新手士兵", 80, 2.5f, "RookieIcon", "Player2") },
            { HeroName.GongNiu, new CharacterBaseAttribute("中士士兵", 90, 3, "SergeantIcon", "Player3") },
            { HeroName.RuiKe, new CharacterBaseAttribute("上尉士兵", 100, 3, "CaptainIcon", "Player1") },
            { HeroName.BuLuoKe, new CharacterBaseAttribute("小精灵", 100, 3, "ElfIcon", "Player1") },
            { HeroName.GeEr, new CharacterBaseAttribute("怪物", 120, 2, "OgreIcon", "Player2") },
            { HeroName.HeiYa, new CharacterBaseAttribute("巨魔", 200, 1, "TrollIcon", "Player3") }
        };
    }
    private void InitWeaponBaseAttr()
    {
        mWeaponBaseAttrDict = new Dictionary<HeroName, WeaponBaseAttribute>
        {
            { HeroName.DaLiEr, new WeaponBaseAttribute("手枪", "WeaponGun", WeaponType.Gun, 0.5f, 2, 6, 0, 30, 90, 45, 16, 15, 0.1f) },
            { HeroName.GongNiu, new WeaponBaseAttribute("长枪", "WeaponRifle", WeaponType.Rifle, 0.5f, 1, 4, 0, 50, 45, 40, 8, 10, 0.01f) },
            { HeroName.RuiKe, new WeaponBaseAttribute("火箭", "WeaponRocket", WeaponType.Rocket, 1f, 1, 10, 0.04f, 5, 400, 0, 13, 1, 0.1f) }
        };
    }
    public CharacterBaseAttribute GetCharacterBaseAttr(HeroName hero)
    {
        if(mCharacterBaseAttrDict.ContainsKey(hero))
        {
            return mCharacterBaseAttrDict[hero];
        }
        else
        {
            Logging.HYLDDebug.LogError("无法根据类型:" + hero + "得到角色基础属性(GetCharacterBaseAttr)"); return null;
        }
    }


    WeaponBaseAttribute IAttributeFactory.GetWeaponBaseAttr(HeroName hero)
    {
        if (mWeaponBaseAttrDict.ContainsKey(hero))
        {
            return mWeaponBaseAttrDict[hero];
        }
        else
        {
            Logging.HYLDDebug.LogError("无法根据类型:" + hero + "得到武器基础属性"); return null;
        }
    }
}
