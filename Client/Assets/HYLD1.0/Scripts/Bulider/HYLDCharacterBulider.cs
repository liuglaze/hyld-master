/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/11/16 13：51 
 * Description:  角色建造者
 * * * * * * * * * * * * * * * * 
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HYLDCharacterBulider
{
    protected ICharacter mCharacter;
    protected HeroName mHeroName;
    protected WeaponType mWeaponType;
    protected Vector3 mSpawnPosition;
    protected string mPrefabsName = "";
    public  HYLDCharacterBulider(ICharacter character,Vector3 spawnPosition, HeroName heroName,WeaponType weaponType)
    {
        mCharacter = character;
        mHeroName = heroName;
        mWeaponType = weaponType;
        mSpawnPosition = spawnPosition;
        
    }
    public void  AddCharacterBaseAttribute()
    {
        //创建角色属性
        CharacterBaseAttribute characterBaseAttribute = FactoryManager.AttributeFactory.GetCharacterBaseAttr(mHeroName);
        mPrefabsName = characterBaseAttribute.PrefabName;
        mCharacter.Attribute = characterBaseAttribute;
    }
    public void AddGameObect()
    {
        //创建角色游戏物体
        //1，加载 2，实例化
        GameObject hero = FactoryManager.ResourcesAssetFactory.LoadSoldier(mPrefabsName);
        hero.transform.position = mSpawnPosition;
        mCharacter.gameObject = hero;
    }
    public void AddWeapon()
    {
        IWeapon weapon = FactoryManager.WeaponFactory.CreateWeapon(mHeroName);
        mCharacter.Weapon = weapon;
    }
    public ICharacter GetResult()
    {
        return mCharacter;
    }

}
