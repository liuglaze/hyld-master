using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CharacterSystem : IGameSystem
{
    private List<ICharacter> mCharacters=new List<ICharacter>();
    public void AddCharacter(ICharacter character)
    {
        mCharacters.Add(character);
    }
    public void  RemoveCharacter(ICharacter character)
    {
        mCharacters.Remove(character);
    }
    public override void Update()
    {
        foreach(ICharacter character in mCharacters)
        {
            character.Update(mCharacters);
        }
    }
}
