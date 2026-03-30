using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
public class EndInformationShow : MonoBehaviour
{
    public string name="null";
    public int baseValue=0;
    public int multipleValue=0;
    public int goldValue = 0;
    public bool islandLoad=false;
    public bool needUpdate = true;
    
    public Text nameText;
    public Text baseValueText;
    public Text multipleValueText;
    public Text goldValueText;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    void changeInformation()
    {
        if(islandLoad)
            gameObject.GetComponentInChildren<Image>().enabled = true;
        else 
            gameObject.GetComponentInChildren<Image>().enabled = false;
        //goldValue = baseValue * multipleValue;
        nameText.text = name;
        baseValueText.text = baseValue.ToString();
        multipleValueText.text = multipleValue.ToString();
        goldValueText.text = goldValue.ToString();
    }
    // Update is called once per frame
    void Update()
    {
        if (needUpdate)
        {
            changeInformation();
            needUpdate = false;
        }
        
    }
}
