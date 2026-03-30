/****************************************************
    ScriptName:        ZYKJsonModel.cs
    Author:            龙之介
    Emall:        505258140@qq.com
    CreatTime:    2020/12/20 15:4:23
    Description:     从Json获取配置表
*****************************************************/

using System.Collections.Generic;
using UnityEngine;
using System;
using System.IO;

namespace ZYKTool
{
    public class ConfigJsonPath
    {
        public static readonly string PLAYER_CONFIG = Application.streamingAssetsPath + "/Player.json";
        public static readonly string GUN_CONFIG = Application.streamingAssetsPath + "/Gun.json";
        public static readonly string TEST_CONFIG = Application.streamingAssetsPath + "/Test.json";
        public static readonly string BOSS_CONFIG = Application.streamingAssetsPath + "/Boss.json";
    }
    namespace Model
    {

        #region Test
        public class ModelVideoData
        {
            public List<ModelJsonVideoData> datas;
        }
        [Serializable]
        public class ModelJsonVideoData
        {
            public int id;
            public int type;
            public string name;
        }
        #endregion
        #region Gun
        public class ModelGunsData
        {
            public List<ModelGunData> Guns;
        }

        [Serializable]
        public class ModelGunData
        {
            public int damage;
            public float startTimeBtwShots;
            public int Count;
            public string name;
        }
        #endregion

        #region Player
        [Serializable]
        public class Players
        {
            public List<Player> Player;
        }
        [Serializable]
        public class Player
        {
            public float QjiNengLengQue;
            public float Speed;
            public int Blood;
            public float BackForce;
        }
        #endregion

        #region Boss
        [Serializable]
        public class Bosses
        {
            public List<Boss> Boss;
        }
        [Serializable]
        public class Boss
        {

            public int Boss1Blood;
            public int Boss2Blood;
            public int Boss3Blood;
            public int Boss4Blood;
            public int Boss5Blood;

        }
        #endregion
    }


    public class ZYKJsonModelToolBase : ZYKSingleModen<ZYKJsonModelToolBase>
    {
        private List<Model.ModelJsonVideoData> _jsonVideoDatas = new List<Model.ModelJsonVideoData>();
        private List<Model.ModelGunData> _Guns = new List<Model.ModelGunData>();
        private List<Model.Player> _Player = new List<Model.Player>();
        private List<Model.Boss> _Bosses = new List<Model.Boss>();

        private T LoadJson<T>(string path)
        {
            if (File.Exists(path))
            {
                var temp = JsonUtility.FromJson<T>(File.ReadAllText(path));

                return temp;
            }

            Logging.HYLDDebug.LogError(path + "not find");
            return default(T);
        }

        public List<Model.ModelGunData> ZYKSingleModenGetGunsData()
        {
            if (_Guns.Count == 0)
            {
                _Guns = LoadJson<Model.ModelGunsData>(ConfigJsonPath.GUN_CONFIG).Guns;
            }

            return _Guns;
        }
        public List<Model.ModelJsonVideoData> ZYKSingleModenGetJsonVideoData()
        {
            if (_jsonVideoDatas.Count == 0)
            {
                _jsonVideoDatas = LoadJson<Model.ModelVideoData>(ConfigJsonPath.TEST_CONFIG).datas;
            }

            return _jsonVideoDatas;
        }

        public Model.Player ZYKModenGetPlayer()
        {
            if (_Player.Count == 0)
            {
                _Player = LoadJson<Model.Players>(ConfigJsonPath.PLAYER_CONFIG).Player;
            }
            return _Player[0];
        }
        public Model.Boss ZYKModenGetBoss()
        {
            if (_Bosses.Count == 0)

            {
                _Bosses = LoadJson<Model.Bosses>(ConfigJsonPath.BOSS_CONFIG).Boss;
            }

            return _Bosses[0];
        }
    }
}