using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using SpeechLib;
using UnityEngine.UI;
using System.Threading;     

public class ToAudio : MonoBehaviour
{
    private SpVoice spVoice_qucik;
    private SpeechVoiceSpeakFlags sp_flag = SpeechVoiceSpeakFlags.SVSFlagsAsync;
    void Awake()
    {
        spVoice_qucik=new SpVoice();
        
    }

    private static ToAudio _instance;
    public static ToAudio Instance { get {
        if (_instance == null)
        {
            _instance = GameObject.Find("MainGameController").GetComponent<ToAudio>();
        }
        return _instance;
    } }
    
    public void SpackStr(string str)
    {
        object s=str;
        Thread thr = new Thread(Text,262144);
        thr.Start(s);
    }
    
    
    void Text(object data)
    {
     //   spVoice_qucik.Voice = spVoice_qucik.GetVoices(string.Empty, string.Empty).Item(0);
        
     //  spVoice_qucik.Speak(data.ToString(),sp_flag);
    }

	
     void Update () {
         if (Input.GetKeyDown("d"))
         {
             PlayerPrefs.DeleteAll();
             
         }
    }
}