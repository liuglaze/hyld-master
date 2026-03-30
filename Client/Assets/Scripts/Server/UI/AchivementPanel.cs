/****************************************************
    Author:            龙之介
    CreatTime:    2021/6/19 22:42:32
    Description:     Nothing
*****************************************************/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using UnityEngine.UI;
using System.Linq;



namespace LongZhiJie
{
	public class AchivementPanel :MonoBehaviour
	{
        private Text Timer;
        private int TotalTime;
        private int mSec;
        private int mMinute;
        private int mHour;
        private float waitTime = 1 / 60;
        private Text BossCountTest;
        private int bosscount = 0;
        private bool isStop = false;
        public void Init()
        {
            Timer = transform.Find("Timer").gameObject.GetComponent<Text>();
            Timer.text = Timer.text = string.Format("{0:d2}:{1:d2}:{2:d2}", mHour, mMinute, mSec);
            BossCountTest = transform.Find("BossCount").gameObject.GetComponent<Text>();
            BossCountTest.text = bosscount.ToString();
            StartCoroutine(CountDown());
            isStop = true;
        }
        public void StopCountDown()
        {
            isStop = true;
            bosscount++;
            BossCountTest.text = bosscount.ToString();
        }
        public void StartCountDown()
        {
            isStop = false;
        }
        IEnumerator CountDown()
        {
            while (true)
            {
                if (isStop) { 
                    yield return new WaitForSeconds(waitTime);
                    continue;
                }
                Timer.text = string.Format("{0:d2}:{1:d2}:{2:d2}", mHour, mMinute, mSec);
                yield return new WaitForSeconds(waitTime);
                TotalTime++;
                mSec = TotalTime;
                if (mSec >= 60)
                {
                    mMinute++;
                    mSec = 0;
                    TotalTime = 0;
                }
                if (mMinute >= 60)
                {
                    mHour++;
                    mMinute = 0;
                }
                //小时暂不处理--根据各自项目决定
            }
            HandleData();
        }
        public void HandleData(bool isSkillAllBoss=false)
        {
            if (isSkillAllBoss) bosscount++;
            //Logging.HYLDDebug.LogError(string.Format("{0:d2}:{1:d2}:{2:d2}", mHour, mMinute, mSec)+"   "+bosscount);
            PlayerPrefs.SetString(PlayerPrefabsEnum.SkillBossTime.ToString(), string.Format("{0:d2}:{1:d2}:{2:d2}", mHour, mMinute, mSec));
            PlayerPrefs.SetInt(PlayerPrefabsEnum.SkillBossCount.ToString(), PlayerPrefs.GetInt(PlayerPrefabsEnum.SkillBossCount.ToString(), 0) + bosscount);
            PlayerPrefs.SetInt(PlayerPrefabsEnum.SkillBossMaxCount.ToString(), Mathf.Max(PlayerPrefs.GetInt(PlayerPrefabsEnum.SkillBossMaxCount.ToString(), 0), bosscount));
        }
    }
}