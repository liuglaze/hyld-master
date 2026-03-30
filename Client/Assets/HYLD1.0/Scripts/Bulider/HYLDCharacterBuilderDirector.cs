/*
 * * * * * * * * * * * * * * * * 
 * Author:        赵元恺
 * CreatTime:  2020/11/16 13：51 
 * Description:  角色建造指挥者
 * * * * * * * * * * * * * * * * 
*/
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HYLDCharacterBuilderDirector
{
    public static ICharacter Construct(HYLDCharacterBulider bulider)
    {
        bulider.AddCharacterBaseAttribute();//添加属性
        bulider.AddGameObect();//添加英雄
        bulider.AddWeapon();//添加武器

        return bulider.GetResult();
    }
}
