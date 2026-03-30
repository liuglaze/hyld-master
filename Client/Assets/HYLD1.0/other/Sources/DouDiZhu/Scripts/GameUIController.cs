using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GameUIController : MonoBehaviour
{
    public List<GameObject> panals;

    private void Start()
    {
        showPannal("hallPanal");
    }

    public void showPannal(string panal)
    {
        foreach (GameObject pos in panals)
        {
            if(pos.name==panal)pos.SetActive(true);
            else pos.SetActive(false);
        }
    }
    public void sendStringMessage(string str)
    {
        MessageController.sendStringMessage(str, MessageTypes.Login);
    }
}
