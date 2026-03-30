/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/11/7 2：38 
 * Description:  工厂管理者
 * * * * * * * * * * * * * * * * 
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class FactoryManager 
{
    private static IResourcesAssetFactory mAssetFactory;
    private static IWeaponFactory mWeaponFactory;
    private static IAttributeFactory mAttributeFactory;
    private static ICharacterFactory mCharacterFactory;
    public static IResourcesAssetFactory ResourcesAssetFactory
    {
        get
        {
            if (mAssetFactory == null)
            {
                mAssetFactory = new ResourcesAssetFactory();
            }
            return mAssetFactory;
        }
        
    }
    public static IWeaponFactory WeaponFactory
    {
        get
        {
            if (mWeaponFactory == null)
            {
                mWeaponFactory = new WeaponFactory();
            }
            return mWeaponFactory;
        }

    }
    public static IAttributeFactory AttributeFactory
    {
        get
        {
            if (mAttributeFactory == null)
            {
                mAttributeFactory = new AttributeFactory();
            }
            return mAttributeFactory;
        }

    }
    public static ICharacterFactory CharacterFactory
    {
        get
        {
            if(mCharacterFactory==null)
            {
                mCharacterFactory = new CharacterFactory();
            }
            return mCharacterFactory;
        }
    }
}
