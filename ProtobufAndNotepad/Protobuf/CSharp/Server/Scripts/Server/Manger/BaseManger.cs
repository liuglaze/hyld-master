/****************************************************
    Author:            龙之介
    CreatTime:    #CreateTime#
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
	public class BaseManger 
	{
        protected GameFace _face;
        public BaseManger(GameFace gameFace)
        {
            _face = gameFace;
        }
        public virtual void OnInit()
        {
            
        }
        public virtual void OnDestroy()
        {
            
        }

	}
}