/*
 * * * * * * * * * * * * * * * * 
 * Author:        ÕÔÔªâý
 * CreatTime:  2020/11/7 2£º38 
 * Description:  ÎäÆ÷¹¤³§
 * * * * * * * * * * * * * * * * 
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface IWeaponFactory
{
    IWeapon CreateWeapon(HeroName hero);

}
public class WeaponFactory : IWeaponFactory
{
    public IWeapon CreateWeapon(HeroName hero)
    {
        IWeapon mIWeapon = null;
        WeaponBaseAttribute baseAttr = FactoryManager.AttributeFactory.GetWeaponBaseAttr(hero);
        GameObject weaponGO = FactoryManager.ResourcesAssetFactory.LoadWeapon(baseAttr.assetName);
        switch (baseAttr.weaponType)
        {
            case WeaponType.Gun:
                mIWeapon = new WeaponGun(baseAttr, weaponGO);
                break;
            case WeaponType.Rifle:
                mIWeapon = new WeaponRifle(baseAttr, weaponGO);
                break;
            case WeaponType.Rocket:
                mIWeapon = new WeaponRocket(baseAttr, weaponGO);
                break;
        }
        return mIWeapon;
    }

}
