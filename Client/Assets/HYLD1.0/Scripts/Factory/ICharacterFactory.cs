using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface  ICharacterFactory 
{
    ICharacter CreateCharacter(WeaponType weaponType, Vector3 spawnPosition, HeroName heroName, int lv = 1);
}
public class CharacterFactory : ICharacterFactory
{
    public ICharacter CreateCharacter(WeaponType weaponType, Vector3 spawnPosition,HeroName heroName, int lv = 1)
    {

        ICharacter character = new HYLDCharacter();
        
        HYLDCharacterBulider bulider = new HYLDCharacterBulider(character, spawnPosition, heroName, weaponType);
        
        return HYLDCharacterBuilderDirector.Construct(bulider);
    }

     
}
