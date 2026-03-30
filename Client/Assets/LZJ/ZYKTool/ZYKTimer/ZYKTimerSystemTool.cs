/****************************************************
    ScriptName:        Invoke.cs
    Author:            龙之介
    Emall:        505258140@qq.com
    CreatTime:    2020/12/14 21:1:19
    Description:     计时器
*****************************************************/

using System;
using System.Threading.Tasks;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;



namespace ZYKTool.Timer
{

    public class PETimeTask
    {
        public int tid;
        public Action callBack;// 要执行什么任务
        public float destTime;//多少时间后执行任务 单位为毫秒
        public float count;//执行任务的次数
        public float delay;//延迟的时间

        public PETimeTask(int tid, Action callBack, float destTime, float delay, float count)
        {
            this.tid = tid;
            this.callBack = callBack;
            this.destTime = destTime;
            this.count = count;
            this.delay = delay;
        }
    }

    public enum PETimeUnit
    {
        Millise,
        Second,
        Minute,
        Hour,
        Day
    }


    public class ZYKTimerSystemTool : MonoBehaviour
    {
        private int tid;//全局ID

        private static readonly string obj = "lock";

        public static ZYKTimerSystemTool Instance { get; private set; }
        private List<PETimeTask> mTimerTaskList = new List<PETimeTask>();

        //临时缓存列表
        private List<PETimeTask> mTempTimeTaskList = new List<PETimeTask>();

        //存储tid
        private List<int> tidList = new List<int>();

        //存储需清理的ID
        private List<int> recIDList = new List<int>();

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(this);
        }


        //实现基础定时任务
        private void Update()
        {
        //    Logging.HYLDDebug.LogError(1);
            //Update();
            //加入缓存区中的定时任务
            for (int tempIndex = 0; tempIndex < mTempTimeTaskList.Count; tempIndex++)
            {
                mTimerTaskList.Add(mTempTimeTaskList[tempIndex]);
            }
            mTempTimeTaskList.Clear();

            //遍历检测任务是否达到条件
            for (int i = 0; i < mTimerTaskList.Count; i++)
            {
            
                PETimeTask timeTask = mTimerTaskList[i];
                //任务时间超过当前时间
                if (timeTask.destTime > Time.realtimeSinceStartup * 1000)
                {
                    continue;
                }
                else
                {
                    Action action = timeTask.callBack;
                    if (action != null)
                    {
                        action();
                    }
                    //移除已经完成的任务
                    if (timeTask.count == 1)
                    {
                        mTimerTaskList.RemoveAt(i);
                        i--;
                        recIDList.Add(timeTask.tid);
                    }
                    else
                    {
                        if (timeTask.count != 0)
                        {
                            timeTask.count -= 1;
                        }
                        timeTask.destTime += timeTask.delay;
                    }
                }
            }

            //当需清理的ID缓存区有东西时，进行清理
            if (recIDList.Count > 0)
            {
                ZYKTimerRecycleTid();
            }
        }
        public void ZYKTimerClearAllTaskAndId()
        {
            mTempTimeTaskList.Clear();
            mTimerTaskList.Clear();
            tidList.Clear();
            recIDList.Clear();
        }
        //清除计时器任务
        private void ZYKTimerRecycleTid()
        {
            for (int i = 0; i < recIDList.Count; i++)
            {
                int tid = recIDList[i];

                for (int j = 0; j < tidList.Count; j++)
                {
                    if (tid == tidList[j])
                    {
                        tidList.RemoveAt(j);
                        break;
                    }
                }
            }
            recIDList.Clear();
        }

        //添加计时器任务
        public int ZYKTimerAddTimerTask(Action callBack, float delay, int count = 1, PETimeUnit timeUnit = PETimeUnit.Second)
        {
            //Logging.HYLDDebug.LogError(2);
            // Logging.HYLDDebug.LogError(56);
            if (timeUnit != PETimeUnit.Millise)
            {
                switch (timeUnit)
                {
                    case PETimeUnit.Second:
                        delay = delay * 1000;
                        break;
                    case PETimeUnit.Minute:
                        delay = delay * 1000 * 60;
                        break;
                    case PETimeUnit.Hour:
                        delay = delay * 1000 * 60 * 60;
                        break;
                    case PETimeUnit.Day:
                        delay = delay * 1000 * 60 * 60 * 24;
                        break;
                    default:
                        Logging.HYLDDebug.LogWarning("Time Task Type Is Error...");
                        break;
                }
            }

            int tid = ZYKTimerGetId();

            float destTime = Time.realtimeSinceStartup * 1000 + delay;
            mTempTimeTaskList.Add(new PETimeTask(tid, callBack, destTime, delay, count));
            tidList.Add(tid);
            return tid;
        }
        //删除计时器任务
        public bool ZYKTimerDelectTimeTask(int tid)
        {
            bool exist = false;

            for (int i = 0; i < mTimerTaskList.Count; i++)
            {
                if (tid == mTimerTaskList[i].tid)
                {
                    mTimerTaskList.RemoveAt(i);
                    for (int j = 0; j < tidList.Count; j++)
                    {
                        if (tid == tidList[j])
                        {
                            tidList.RemoveAt(j);
                            break;
                        }
                    }
                    exist = true;
                    break;
                }
            }

            if (!exist)
            {
                for (int i = 0; i < mTempTimeTaskList.Count; i++)
                {
                    if (tid == mTempTimeTaskList[i].tid)
                    {
                        mTempTimeTaskList.RemoveAt(i);
                        for (int j = 0; j < tidList.Count; j++)
                        {
                            if (tid == tidList[j])
                            {
                                tidList.RemoveAt(j);
                                break;
                            }
                        }
                        exist = true;
                        break;
                    }
                }
            }

            return exist;
        }
        //替换计时器任务
        public bool ZYKTimerReplaceTimeTask(int tid, Action callBack, float delay, int count = 1, PETimeUnit timeUnit = PETimeUnit.Millise)
        {
            if (timeUnit != PETimeUnit.Millise)
            {
                switch (timeUnit)
                {
                    case PETimeUnit.Second:
                        delay = delay * 1000;
                        break;
                    case PETimeUnit.Minute:
                        delay = delay * 1000 * 60;
                        break;
                    case PETimeUnit.Hour:
                        delay = delay * 1000 * 60 * 60;
                        break;
                    case PETimeUnit.Day:
                        delay = delay * 1000 * 60 * 60 * 24;
                        break;
                    default:
                        Logging.HYLDDebug.LogWarning("Time Task Type Is Error...");
                        break;
                }
            }
            bool isReplace = false;
            float destTime = Time.realtimeSinceStartup * 1000 + delay;
            PETimeTask newTask = new PETimeTask(tid, callBack, destTime, delay, count);

            for (int i = 0; i < mTimerTaskList.Count; i++)
            {
                if (mTimerTaskList[i].tid == tid)
                {
                    mTimerTaskList[i] = newTask;
                    isReplace = true;
                    break;
                }
            }

            if (!isReplace)
            {
                for (int i = 0; i < mTempTimeTaskList.Count; i++)
                {
                    if (mTempTimeTaskList[i].tid == tid)
                    {
                        mTempTimeTaskList[i] = newTask;
                        isReplace = true;
                        break;
                    }
                }
            }
            return isReplace;
        }
        //获得id
        private int ZYKTimerGetId()
        {
            //添加计时任务为多线程，多线程生成唯一ID时需要锁防止同时访问生成
            lock (obj)
            {
                tid += 1;

                //安全检测 以防万一
                while (true)
                {
                    //tid达到int的最大值时
                    if (tid == int.MaxValue)
                    {
                        tid = 0;
                    }

                    bool isUsed = false;

                    for (int i = 0; i < tidList.Count; i++)
                    {
                        if (tid == tidList[i])
                        {
                            isUsed = true;
                            break;
                        }
                    }
                    if (!isUsed)
                    {
                        break;
                    }
                    else
                    {
                        tid += 1;
                    }
                }
            }

            return tid;
        }
    }
}