/****************************************************
    Author:            龙之介
    CreatTime:    2021/3/23 15:50:38
    Description:     定点数
*****************************************************/


using System;
using System.Collections.Generic;

namespace LZJ
{
    /// <summary>
    /// Fixed扩展
    /// </summary>
    public static class FixedExtend
    {
        public static LZJ.Fixed ToFixed(this Int32 i)
        {
            return new LZJ.Fixed(i);
        }
        public static LZJ.Fixed ToFixed(this float f)
        {
            return new LZJ.Fixed(f);
        }
        public static LZJ.Fixed2 ToFixed2(this UnityEngine.Vector2 v2)
        {
            return new LZJ.Fixed2(v2.x, v2.y);
        }
        public static LZJ.Fixed ToFixedRotation(this UnityEngine.Quaternion rotation)
        {
            return -rotation.eulerAngles.y.ToFixed();
        }

    }

    /// <summary>
    /// 定点数 基于Int64
    /// </summary>
	[System.Serializable]
    public struct Fixed
	{
        /// <summary>
        /// 小数占用位
        /// </summary>
        public static int Fix_Fracbits = 16;

        /// <summary>
        ///  0
        /// </summary>
        public static Fixed Zero = new Fixed(0);

        
        public Int64 m_Bits;

        public Fixed(int x)
        {
            m_Bits = (x << Fix_Fracbits);
        }
        public Fixed(float x)
        {
            m_Bits = (Int64)((x) * (1 << Fix_Fracbits));
        }
        public Fixed(Int64 x)
        {
            m_Bits = ((x) * (1 << Fix_Fracbits));
        }


        public Int64 GetValue()
        {
            return m_Bits;
        }

        public Fixed SetValue(Int64 x)
        {
            m_Bits = x;
            return this;
        }

        /// <summary>
        /// 插值计算
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="t"></param>
        /// <returns></returns>
        public static Fixed Lerp(Fixed a,Fixed b,float t)
        {
            return a + (b - a) * t;
        }
        /// <summary>
        /// 插值计算
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="t"></param>
        /// <returns></returns>
        public static Fixed Lerp(Fixed a,Fixed b,Fixed t)
        {
            return a + (b - a) * t;
        }
        /// <summary>
        /// 旋转360°的插值
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="time"></param>
        /// <returns></returns>
        public static Fixed RotationLerp(Fixed a,Fixed b,Fixed time)
        {
            while(a<0)
            {
                a += 360;
            }
            while(b<0)
            {
                b += 360;
            }

            var offset1 = b - a;
            var offset2 = b - (a + 360);

            return a + time * (offset1.Abs() < offset2.Abs() ? offset1 : offset2);

        }

        public Fixed Abs()
        {
            return Fixed.Abs(this);
        }


        public Fixed Sqrt()
        {
            return Fixed.Sqrt(this);
        }

        public static Fixed Sqrt(Fixed p1)
        {
            Fixed tmp;
            Int64 tmp1 = p1.m_Bits * (1 << Fix_Fracbits);
            tmp.m_Bits = (Int64)Math.Sqrt(tmp1);
            return tmp;
        }
        public static Fixed Pow(Fixed p1,Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = (Int64)(Math.Pow(p1.ToFloat(), p2.ToFloat())*(1 << Fix_Fracbits));

            /*
            Int64 tmp1 = p1.m_Bits;// * (1 << Fix_Fracbits);
            Int64 tmp2 = p2.m_Bits;// * (1 << Fix_Fracbits);
            tmp.m_Bits = (Int64)Math.Pow(tmp1,tmp2);
            //tmp.m_Bits = tmp2 / (float)(1 << Fix_Fracbits);
            */
            return tmp;
        }

        public static Fixed Range(Fixed n, int min, int max)
        {
            if (n < min) n = new Fixed(min);
            if (n > max) n = new Fixed(max);
            return n;
        }


        //********************************   +   ***********************************
        public static Fixed operator +(Fixed p1,Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = p1.m_Bits + p2.m_Bits;
            return tmp;
        }

        public static Fixed operator +(int p1,Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = (Int64)(p1 << Fix_Fracbits) + p2.m_Bits;
            return tmp;
        }

        public static Fixed operator +(Fixed p2,int p1 )
        {
            return p1+p2;
        }
        public static Fixed operator +(Int64 p1,Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = (p1 << Fix_Fracbits) + p2.m_Bits;
            return tmp;
        }
        public static Fixed operator +(Fixed p2, Int64 p1)
        {
            return p1 + p2;
        }
        public static Fixed operator+(float p1,Fixed p2 )
        {
            Fixed tmp;
            tmp.m_Bits = (Int64)(p1 * (1 << Fix_Fracbits)) + p2.m_Bits;
            return tmp;
        }


        public static Fixed operator +(Fixed p2, float p1)
        {
            return p1 + p2;
        }

        //****************** - ****************************
        public static Fixed operator -(Fixed p1, Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = p1.m_Bits - p2.m_Bits;
            return tmp;
        }

        public static Fixed operator -(int p1, Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = (Int64)(p1 << Fix_Fracbits) - p2.m_Bits;
            return tmp;
        }

        public static Fixed operator -(Fixed p1, int p2)
        {
            Fixed tmp;
            tmp.m_Bits = p1.m_Bits - (Int64)(p2 << Fix_Fracbits);
            return tmp;
        }
        public static Fixed operator -(Int64 p1, Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = (p1 << Fix_Fracbits) - p2.m_Bits;
            return tmp;
        }
        public static Fixed operator -(Fixed p2, Int64 p1)
        {
            Fixed tmp;
            tmp.m_Bits = p2.m_Bits - (p1 << Fix_Fracbits) ;
            return tmp;
        }
        public static Fixed operator -(float p1, Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = (Int64)(p1 * (1 << Fix_Fracbits)) - p2.m_Bits;
            return tmp;
        }


        public static Fixed operator -(Fixed p2, float p1)
        {
            Fixed tmp;
            tmp.m_Bits = p2.m_Bits-(Int64)(p1 * (1 << Fix_Fracbits)) ;
            return tmp;
        }

        //****************** * ****************************
        public static Fixed operator *(Fixed p1, Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = p1.m_Bits * p2.m_Bits;
            return tmp;
        }

        public static Fixed operator *(int p1, Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = p1* p2.m_Bits;
            return tmp;
        }

        public static Fixed operator *(Fixed p1, int p2)
        {
          
            return p2 * p1;
        }

        public static Fixed operator *(float p1, Fixed p2)
        {
            Fixed tmp;
            tmp.m_Bits = (Int64)(p1 * p2.m_Bits);
            return tmp;
        }


        public static Fixed operator *(Fixed p2, float p1)
        {
            Fixed tmp;
            tmp.m_Bits = (Int64)(p1 * p2.m_Bits);
            return tmp;
        }


        //****************** / ****************************
        public static Fixed operator /(Fixed p1, Fixed p2)
        {
            Fixed tmp;
            if(p2 == Fixed.Zero)
            {
                Logging.HYLDDebug.LogWarning("0 cannot be a divisor ");
                tmp.m_Bits = Zero.m_Bits;
            }
            else
            {
                tmp.m_Bits = (p1.m_Bits) * (1 << Fix_Fracbits) / (p2.m_Bits);
            }

            return tmp;
        }

        public static Fixed operator /(int p1, Fixed p2)
        {
            Fixed tmp;
            if (p2 == Fixed.Zero)
            {
                Logging.HYLDDebug.LogWarning("0 cannot be a divisor ");
                tmp.m_Bits = Zero.m_Bits;
            }
            else
            {
                Int64 tmp2 = (Int64)p1 << Fix_Fracbits << Fix_Fracbits;
                tmp.m_Bits = tmp2 / (p2.m_Bits);
            }
            
            return tmp;
        }

        public static Fixed operator /(Fixed p1, int p2)
        {
            Fixed tmp;
            if (p2 == 0)
            {
                Logging.HYLDDebug.LogWarning("0 cannot be a divisor ");
                tmp.m_Bits = Zero.m_Bits;
            }
            else
            {
                tmp.m_Bits = p1.m_Bits / (p2);
            }
            return  tmp;
        }

        public static Fixed operator /(Fixed p1, Int64 p2)
        {
            Fixed tmp;
            if (p2 == 0)
            {
                Logging.HYLDDebug.LogError("/0");
                tmp.m_Bits = Zero.m_Bits;
            }
            else
            {
                tmp.m_Bits = p1.m_Bits / (p2);
            }
            return tmp;
        }
        public static Fixed operator /(Int64 p1, Fixed p2)
        {
            Fixed tmp;
            if (p2 == Zero)
            {
                Logging.HYLDDebug.LogError("/0");
                tmp.m_Bits = Zero.m_Bits;
            }
            else
            {
                if (p1 > Int32.MaxValue || p1 < Int32.MinValue)
                {
                    tmp.m_Bits = 0;
                    return tmp;
                }
                tmp.m_Bits = (p1 << Fix_Fracbits) / (p2.m_Bits);
            }
            return tmp;
        }
        public static Fixed operator /(float p1, Fixed p2)
        {
            Fixed tmp;
            if (p2 == Zero)
            {
                Logging.HYLDDebug.LogError("/0");
                tmp.m_Bits = Zero.m_Bits;
            }
            else
            {
                Int64 tmp1 = (Int64)p1 * ((Int64)1 << Fix_Fracbits << Fix_Fracbits);
                tmp.m_Bits = (tmp1) / (p2.m_Bits);
            }
            return tmp;
        }
        public static Fixed operator /(Fixed p1, float p2)
        {
            Fixed tmp;
            if (p2 > -0.000001f && p2 < 0.000001f)
            {
                Logging.HYLDDebug.LogError("/0");
                tmp.m_Bits = Zero.m_Bits;
            }
            else
            {
                tmp.m_Bits = (p1.m_Bits << Fix_Fracbits) / ((Int64)(p2 * (1 << Fix_Fracbits)));
            }
            return tmp;
        }

        //************  %  *****************************
        public static Fixed operator %(Fixed p1, int p2)
        {
            Fixed tmp;
            if (p2 == 0)
            {
                Logging.HYLDDebug.LogError("/0");
                tmp.m_Bits = Zero.m_Bits;
            }
            else
            {
                tmp.m_Bits = (p1.m_Bits % (p2 << Fix_Fracbits));
            }
            return tmp;
        }



        //************  逻辑运算   *********************
        public static bool operator >(Fixed p1, Fixed p2)
        {
            return (p1.m_Bits > p2.m_Bits) ? true : false;
        }
        public static bool operator <(Fixed p1, Fixed p2)
        {
            return (p1.m_Bits < p2.m_Bits) ? true : false;
        }
        public static bool operator <=(Fixed p1, Fixed p2)
        {
            return (p1.m_Bits <= p2.m_Bits) ? true : false;
        }
        public static bool operator >=(Fixed p1, Fixed p2)
        {
            return (p1.m_Bits >= p2.m_Bits) ? true : false;
        }
        public static bool operator !=(Fixed p1, Fixed p2)
        {
            return (p1.m_Bits != p2.m_Bits) ? true : false;
        }
        public static bool operator ==(Fixed p1, Fixed p2)
        {
            return (p1.m_Bits == p2.m_Bits) ? true : false;
        }



        public static bool operator >(Fixed p1, float p2)
        {
            return (p1.m_Bits > (p2 * (1 << Fix_Fracbits))) ? true : false;
        }
        public static bool operator <(Fixed p1, float p2)
        {
            return (p1.m_Bits < (p2 * (1 << Fix_Fracbits))) ? true : false;
        }
        public static bool operator <=(Fixed p1, float p2)
        {
            return (p1.m_Bits <= p2 * (1 << Fix_Fracbits)) ? true : false;
        }
        public static bool operator >=(Fixed p1, float p2)
        {
            return (p1.m_Bits >= p2 * (1 << Fix_Fracbits)) ? true : false;
        }
        public static bool operator !=(Fixed p1, float p2)
        {
            return (p1.m_Bits != p2 * (1 << Fix_Fracbits)) ? true : false;
        }
        public static bool operator ==(Fixed p1, float p2)
        {
            return (p1.m_Bits == p2 * (1 << Fix_Fracbits)) ? true : false;
        }
        

        public static bool Equals(Fixed p1, Fixed p2)
        {
            return (p1.m_Bits == p2.m_Bits) ? true : false;
        }

        public bool Equals(Fixed right)
        {
            if (m_Bits == right.m_Bits)
            {
                return true;
            }
            return false;
        }
        public override bool Equals(object obj)
        {
            return Equals(obj);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }



        //**************  函数 *******************
        public static Fixed Max()
        {
            Fixed tmp;
            tmp.m_Bits = Int64.MaxValue;
            return tmp;
        }

        public static Fixed Max(Fixed p1, Fixed p2)
        {
            return p1.m_Bits > p2.m_Bits ? p1 : p2;
        }
        public static Fixed Min(Fixed p1, Fixed p2)
        {
            return p1.m_Bits < p2.m_Bits ? p1 : p2;
        }

        public static Fixed Precision()
        {
            Fixed tmp;
            tmp.m_Bits = 1;
            return tmp;
        }

        public static Fixed MaxValue()
        {
            Fixed tmp;
            tmp.m_Bits = Int64.MaxValue;
            return tmp;
        }
        public static Fixed Abs(Fixed P1)
        {
            Fixed tmp;
            tmp.m_Bits = Math.Abs(P1.m_Bits);
            return tmp;
        }
        public static Fixed operator -(Fixed p1)
        {
            Fixed tmp;
            tmp.m_Bits = -p1.m_Bits;
            return tmp;
        }

        public float ToFloat()
        {
            return m_Bits / (float)(1 << Fix_Fracbits);
        }
        public UnityEngine.Quaternion ToUnityRotation()
        {
            return UnityEngine.Quaternion.Euler(0, -this.ToFloat(), 0);
        }
        public int ToInt()
        {
            return (int)(m_Bits >> (Fix_Fracbits));
        }
        public override string ToString()
        {
            double tmp = (double)m_Bits / (double)(1 << Fix_Fracbits);
            return tmp.ToString();
        }


    }


    class MathFixed
    {
        protected static int tabCount = 18 * 4;
        /// <summary>
        /// sin值对应表
        /// </summary>
        protected static readonly List<Fixed> _m_SinTab = new List<Fixed>();
        public static readonly Fixed PI = new Fixed(3.14159265f);
        protected static Fixed GetSinTab(Fixed r)
        {

            Fixed i = new Fixed(r.ToInt());
            //Logging.HYLDDebug.Log(i.ToInt());
            if (i.ToInt() == _m_SinTab.Count - 1)
            {
                return _m_SinTab[(int)i.ToInt()];
            }
            else
            {
                // Logging.HYLDDebug.Log(i.ToInt()+":"+ _m_SinTab[i.ToInt()]+":"+ Ratio.Lerp(_m_SinTab[i.ToInt()], _m_SinTab[(i + 1).ToInt()], r - i));
                return Fixed.Lerp(_m_SinTab[(int)i.ToInt()], _m_SinTab[(int)(i + 1).ToInt()], r - i);
            }

        }
        public static Fixed GetAsinTab(Fixed sin)
        {
            MathFixed math = Instance;
            //Logging.HYLDDebug.Log("GetAsinTab");
            for (int i = _m_SinTab.Count - 1; i >= 0; i--)
            {

                if (sin > _m_SinTab[i])
                {
                    if (i == _m_SinTab.Count - 1)
                    {
                        return new Fixed(i) / (tabCount / 4) * (PI / 2);
                    }
                    else
                    {
                        //return new Ratio(i);
                        return Fixed.Lerp(new Fixed(i), new Fixed(i + 1), (sin - _m_SinTab[i]) / (_m_SinTab[i + 1] - _m_SinTab[i])) / (tabCount / 4) * (PI / 2);
                    }
                }
            }
            return new Fixed();
        }
        protected static MathFixed Instance
        {
            get
            {
                if (_m_instance == null)
                {
                    _m_instance = new MathFixed();

                }
                return _m_instance;
            }
        }
        protected static MathFixed _m_instance;
        protected MathFixed()
        {
            if (_m_instance == null)
            {

                _m_SinTab.Add(new Fixed(0f));//0
                _m_SinTab.Add(new Fixed(0.08715f));
                _m_SinTab.Add(new Fixed(0.17364f));
                _m_SinTab.Add(new Fixed(0.25881f));
                _m_SinTab.Add(new Fixed(0.34202f));//20
                _m_SinTab.Add(new Fixed(0.42261f));
                _m_SinTab.Add(new Fixed(0.5f));

                _m_SinTab.Add(new Fixed(0.57357f));//35
                _m_SinTab.Add(new Fixed(0.64278f));
                _m_SinTab.Add(new Fixed(0.70710f));
                _m_SinTab.Add(new Fixed(0.76604f));
                _m_SinTab.Add(new Fixed(0.81915f));//55
                _m_SinTab.Add(new Fixed(0.86602f));//60

                _m_SinTab.Add(new Fixed(0.90630f));
                _m_SinTab.Add(new Fixed(0.93969f));
                _m_SinTab.Add(new Fixed(0.96592f));
                _m_SinTab.Add(new Fixed(0.98480f));//80
                _m_SinTab.Add(new Fixed(0.99619f));

                _m_SinTab.Add(new Fixed(1f));


            }
        }
        //**************  函数 *******************
        public static Fixed PiToAngel(Fixed pi)
        {
            return pi / PI * 180;
        }
        public static Fixed Asin(Fixed sin)
        {
            if (sin < -1 || sin > 1) { return new Fixed(); }
            if (sin >= 0)
            {
                return GetAsinTab(sin);
            }
            else
            {
                return -GetAsinTab(-sin);
            }
        }
        public static Fixed Sin(Fixed r)
        {

            MathFixed math = Instance;
            //int tabCount = SinTab.Count*4;
            Fixed result = new Fixed();
            r = (r * tabCount / 2 / PI);
            //int n = r.ToInt();
            while (r < 0)
            {
                r += tabCount;
            }
            while (r > tabCount)
            {
                r -= tabCount;
            }
            if (r >= 0 && r <= tabCount / 4)                // 0 ~ PI/2
            {
                result = GetSinTab(r);
            }
            else if (r > tabCount / 4 && r < tabCount / 2)       // PI/2 ~ PI
            {
                r -= new Fixed(tabCount / 4);
                result = GetSinTab(new Fixed(tabCount / 4) - r);
            }
            else if (r >= tabCount / 2 && r < 3 * tabCount / 4)    // PI ~ 3/4*PI
            {
                r -= new Fixed(tabCount / 2);
                result = -GetSinTab(r);
            }
            else if (r >= 3 * tabCount / 4 && r < tabCount)      // 3/4*PI ~ 2*PI
            {
                r = new Fixed(tabCount) - r;
                result = -GetSinTab(r);
            }

            return result;
        }
        public static Fixed Abs(Fixed ratio)
        {
            return Fixed.Abs(ratio);
        }
        public static Fixed Sqrt(Fixed r)
        {
            return Fixed.Sqrt(r);
        }

        public static Fixed Cos(Fixed r)
        {
            return Sin(r + PI / 2);
        }
        public static Fixed SinAngle(Fixed angle)
        {
            return Sin(angle / 180 * PI);
        }
        public static Fixed CosAngle(Fixed angle)
        {
            return Cos(angle / 180 * PI);
        }

        public static UnityEngine.Vector3 xAndY2UnitVector3(float x, float z)
        {
            float sin1 = x / (float.Parse(Math.Sqrt(x * x + z * z).ToString()));
            float cos1 = (float.Parse(Math.Sqrt(1 - sin1 * sin1).ToString()));
            //LZJ.Fixed sin1 = x / LZJ.MathFixed.Sqrt(x * x + z * z);
            //LZJ.Fixed cos1 = LZJ.MathFixed.Sqrt(1 - sin1 * sin1);
            //return new UnityEngine.Vector3(sin1.ToFloat(), 0, z > 0 ? cos1.ToFloat() : -cos1.ToFloat());
            return new UnityEngine.Vector3(sin1, 0, z > 0 ? cos1 : -cos1);
        }
        public static UnityEngine.Vector3 Vector32UnitVector3(UnityEngine.Vector3 start, UnityEngine.Vector3 end)
        {
            float x = end.x - start.x;
            float z = end.z - start.z;
            float sin1 = x/(float.Parse(Math.Sqrt(x * x + z * z).ToString()));
            float cos1 = (float.Parse(Math.Sqrt(1 - sin1 * sin1).ToString()));
            //float sin1 = x / Sqrt(x * x + z * z);
            //float cos1 = Sqrt(1 - sin1 * sin1);

            return new UnityEngine.Vector3(sin1, 0, z > 0 ? cos1 : -cos1);
        }
    }


    /// <summary>
    ///  定点数二维向量
    /// </summary>
    [Serializable]
    public struct Fixed2
    {
        public Fixed x;
        public Fixed y;

        public static Fixed2 zero = new Fixed2(0, 0);
        public static Fixed2 one = new Fixed2(1, 1);
        public static Fixed2 left = new Fixed2(-1, 0);
        public static Fixed2 right = new Fixed2(1, 0);
        public static Fixed2 up = new Fixed2(0, 1);
        public static Fixed2 down = new Fixed2(0, -1);
        public Fixed2(Fixed x,Fixed y)
        {
            this.x = x;
            this.y = y;
        }
        public Fixed2(float x,float y)
        {
            this.x = new Fixed(x);
            this.y = new Fixed(y);
        }

        public Fixed2 normalized
        {
            get
            {
                if (x == 0 && y == 0)
                {
                    return new Fixed2();
                }
                Fixed n = ((x * x) + (y * y)).Sqrt();

                var result = new Fixed2(x / n, y / n);
                result.x = Fixed.Range(result.x, -1, 1);
                result.y = Fixed.Range(result.y, -1, 1);
                return result;
            }
        }


        public Fixed magnitude
        {
            get
            {
                if (x == 0 && y == 0)
                {
                    return Fixed.Zero;
                }
                Fixed n = ((x * x) + (y * y)).Sqrt();
                return n;
            }
        }

        public UnityEngine.Vector3 ToVector3(int zValue=0)
        {
            return new UnityEngine.Vector3(x.ToFloat(), y.ToFloat(), zValue);
        }

        public static Fixed2 GetVector2(Fixed x,Fixed y)
        {
            return new Fixed2(x, y);
        }

        //**************  重载运算符 *******************
        public static Fixed2 operator +(Fixed2 a,Fixed2 b )
        {
            return new Fixed2(a.x + b.x,a.y + b.y);
        }

        public static Fixed2 operator -(Fixed2 a,Fixed2 b)
        {
            return new Fixed2(a.x - b.x, a.y - b.y);
        }

        public static Fixed2 operator *(Fixed2 a, Fixed b)
        {
            return new Fixed2(a.x * b, a.y * b);
        }





        public static Fixed2 operator -(Fixed2 a)
        {
            return new Fixed2(-a.x, -a.y);
        }
        public static Fixed3 operator *(Fixed2 a, Fixed2 b)
        {
            return new Fixed3(new Fixed(),new Fixed(), a.x * b.y - a.y * b.x);
        }
        public static bool operator ==(Fixed2 a, Fixed2 b)
        {
            return a.x == b.x && a.y == b.y;
        }
        public static bool operator !=(Fixed2 a, Fixed2 b)
        {
            return a.x != b.x || a.y != b.y;
        }

        //**************  函数 *******************


        /// <summary>
        /// 使得某roation转成向量
        /// </summary>
        /// <param name="ratation"></param>
        /// <returns></returns>
        public static Fixed2 Parse(Fixed ratation)
        {
            return new Fixed2(MathFixed.CosAngle(ratation), MathFixed.SinAngle(ratation));
        }
        /// <summary>
        /// 向量点积
        /// </summary>
        /// <param name="b"></param>
        /// <returns></returns>
        public Fixed Dot(Fixed2 b)
        {
            return Dot(this, b);
        }
        /// <summary>
        /// 向量点积
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static Fixed Dot(Fixed2 a, Fixed2 b)
        {
            return a.x * b.x + b.y * a.y;
        }

        /// <summary>
        /// 叉乘
        /// </summary>
        /// <param name="b"></param>
        /// <returns></returns>
        public Fixed Cos(Fixed2 b)
        {
            return Cos(this, b);
        }
        /// <summary>
        /// 叉乘
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static Fixed Cos(Fixed2 a, Fixed2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        /// <summary>
        /// 使得当前点旋转value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public Fixed2 Rotate(Fixed value)
        {
            Fixed tx, ty;
            tx = MathFixed.CosAngle(value) * x - y * MathFixed.SinAngle(value);
            ty = MathFixed.CosAngle(value) * y + x * MathFixed.SinAngle(value);
            return new Fixed2(tx, ty);
        }

        public Fixed ToRotation()
        {
            if (x == 0 && y == 0)
            {
                return new Fixed();
            }
            Fixed sin = this.normalized.y;
            Fixed result = Fixed.Zero;

            if(this.x>=0)
            {
                result = MathFixed.Asin(sin) / MathFixed.PI * 180 + 180;
            }
            else
            {
                result= MathFixed.Asin(-sin) / MathFixed.PI * 180 + 180;
            }
            return result;

        }
        /// <summary>
        /// 返回a点到b点的距离
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static Fixed Distance(Fixed2 a,Fixed2 b)
        {
            return ((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y)).Sqrt();
        }
        public override string ToString()
        {
            return "{" + x.ToString() + "," + y.ToString() + "}";// + ":" + ToVector3().ToString();
        }

        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            
            return base.GetHashCode();
        }




    }

    /// <summary>
    /// 定点数三维向量
    /// </summary>
    [Serializable]
    public struct Fixed3
    {
        public Fixed x
        {
            get;
            private set;
        }
        public Fixed y
        {
            get;
            private set;
        }
        public Fixed z
        {
            get;
            private set;
        }
        public Fixed magnitude
        {
            get
            {
               // Logging.HYLDDebug.Log($"{x}  {y}  {z} pow: {Fixed.Pow(x, new Fixed(2))}  {Fixed.Pow(y, new Fixed(2))}    {Fixed.Pow(z, new Fixed(2))}");
                return MathFixed.Sqrt(Fixed.Pow(x, new Fixed(2)) + Fixed.Pow(y, new Fixed(2)) + Fixed.Pow(z, new Fixed(2)));
            }
        }
        public Fixed3(UnityEngine.Vector3 vector3)
        {
            this.x = new Fixed(vector3.x);
            this.y = new Fixed(vector3.y);
            this.z = new Fixed(vector3.z);
        }
        public Fixed3(int x=0,int y=0,int z=0)
        {
            this.x = new Fixed(x);
            this.y = new Fixed(y);
            this.z = new Fixed(z);
        }
        public Fixed3(float x, float y, float z)
        {

            this.x = new Fixed(x);
            this.y = new Fixed(y);
            this.z = new Fixed(z);

        }
        public Fixed3(Fixed x, Fixed y, Fixed z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
        public static Fixed3 left = new Fixed3(-1, 0);
        public static Fixed3 right = new Fixed3(1, 0);
        public static Fixed3 up = new Fixed3(0, 1);
        public static Fixed3 down = new Fixed3(0, -1);
        public static Fixed3 zero = new Fixed3(0, 0);






        //**************  重载运算符 *******************
        public static Fixed3 operator +(Fixed3 a, Fixed3 b)
        {
            return new Fixed3(a.x + b.x, a.y + b.y, a.z + b.z);
        }
        public static Fixed3 operator -(Fixed3 a, Fixed3 b)
        {
            return new Fixed3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        public static Fixed3 operator -(Fixed3 a)
        {
            return new Fixed3(-a.x, -a.y, -a.z);
        }
        /// <summary>
        /// 三个向量叉乘  
        /// (a×b)×c = nb - ma = (a*c)b -  (b*c)a 
        /// = (             a*c           )b   -  (             b*c              )a 
        /// = (a1c1+a2c2)b1i + (a1c1+a2c2)b2j  -  (b1c1+b2c2)a1i - (b1c1+b2c2)a2j
        /// = (a1c1b1+a2c2b1 - b1c1a1-b2c2a1)i +  (a1c1b2+a2c2b2 -  b1c1a2-b2c2a2)j
        /// = (a2c2b1        - b2c2a1       )i +  (a1c1b2        -  b1c1a2       )j
        /// = ( -(a1b2-a2b1)c2              )i +  (          (a1b2-b1a2)c1       )j
        /// = ( - (a   x  b ) c2            )i +  (          (a   x  b )c1       )j
        /// 
        /// 
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static Fixed2 operator *(Fixed3 a, Fixed2 c)
        {
            return new Fixed2(-a.z * c.y, a.z * c.x);
        }

        public static Fixed3 operator *(Fixed3 a, Fixed c)
        {
            return new Fixed3(a.x * c, a.y * c, a.z * c);
        }
        public static Fixed3 operator *(Fixed3 a, float c)
        {
            return new Fixed3(a.x * c, a.y * c, a.z * c);
        }

        //**************  函数 *******************
        public Fixed Dot(Fixed3 b)
        {
            return Dot(this, b);
        }
        public static Fixed Dot(Fixed3 a, Fixed3 b)
        {
            return a.x * b.x + b.y * a.y;
        }
        public static Fixed2 Cos(Fixed3 a, Fixed2 b)
        {
            return new Fixed2(-a.z * b.y, a.z * b.x);
        }

        public UnityEngine.Vector3 ToVector3()
        {
            return new UnityEngine.Vector3(x.ToFloat(), y.ToFloat(), z.ToFloat());
        }
        public override string ToString()
        {
            return "{" + x.ToString() + "," + y.ToString() +","+z.ToFloat()+ "}";
        }
    }

    /// <summary>
    /// 定点数矩形区域
    /// </summary>
    [Serializable]
    public struct RectFixed
    {

        //   source:矩阵
        /// <summary>
        /// 参数:
        ///   source:矩阵
        /// </summary>
        /// <param name="source"></param>
        public RectFixed(RectFixed source)
        {
            this = source;
        }


        /// <summary>
        /// 摘要:
        ///     创造一个矩形，通过给予position位置和size大小
        /// </summary>
        /// <param name="position"></param>
        /// <param name="size"></param>
        public RectFixed(Fixed2 position, Fixed2 size)
        {
            this.position = position;
            this.size = size;
            height = size.y;
            width = size.x;
            x = position.x;
            y = position.y;
            xMin = x;
            xMax = x + width;
            yMin = y;
            yMax = y + height;
            center = position + new Fixed2(width / 2, height / 2);
        }

        /// <summary>
        /// 摘要：
        ///     创建一个矩形。通过给予x坐标，y坐标，width宽，height高
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        public RectFixed(Fixed x, Fixed y, Fixed width, Fixed height)
        {
            this.x = x;
            this.y = y;
            this.height = height;
            this.width = width;
            this.position = new Fixed2(x,y);
            this.size = new Fixed2(width,height);
            center = position + new Fixed2(width/ 2, height/ 2);
            xMin = x;
            xMax = x + width;
            yMin = y;
            yMax = y + height;
        }
        /// <summary>
        /// rect（0，0，0，0）
        /// </summary>
        public static RectFixed zero { get; }

        /// <summary>
        /// y方向最大值
        /// </summary>
        public Fixed yMax { get; set; }
        /// <summary>
        /// x方向最大值
        /// </summary>
        public Fixed xMax { get; set; }
        /// <summary>
        /// y方向最小值
        /// </summary>
        public Fixed yMin { get; set; }
        /// <summary>
        /// x方向最小值
        /// </summary>
        public Fixed xMin { get; set; }
        /// <summary>
        /// x坐标
        /// </summary>
        public Fixed x { get; set; }
        /// <summary>
        ///  矩形高
        /// </summary>
        public Fixed height { get; set; }
       /// <summary>
       /// 矩形宽
       /// </summary>
        public Fixed width { get; set; }
        /// <summary>
        /// 矩形中心
        /// </summary>
        public Fixed2 center { get; set; }
        
        /// <summary>
        /// 由x，y组成的坐标
        /// </summary>
        public Fixed2 position { get; set; }

        /// <summary>
        /// y 坐标
        /// </summary>
        public Fixed y { get; set; }

        /// <summary>
        /// 描述矩形大小的二维定点数
        /// </summary>
        public Fixed2 size { get; set; }

        /// <summary>
        ///    The Pos IsContains this Rect
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public bool Contains(Fixed2 pos)
        {
            if(x<pos.x&&pos.x<x+width&&y>pos.y&&pos.y<height)
            {
                return true;
            }
            return false;
        }
        /*
        /// <summary>
        /// 单纯形是否在矩形
        /// </summary>
        /// <param name="shap"></param>
        /// <returns></returns>
        public bool Contains(ShapeBase shap)
        {
            if((center.x-shap.position.x).Abs()<=(shap.width/2+width/2)
                &&((center.y-shap.position.y).Abs()<= (shap.heigh/2+height/2)))
            {
                return true;
            }
            return false;
        }
        */
        /// <summary>
        /// 矩形是否在矩形
        /// </summary>
        /// <param name="rect"></param>
        /// <returns></returns>
        public bool Overlaps(RectFixed rect)
        {
            if((rect.center.x-center.x).Abs()<=(rect.width/2+width/2)
                &&((center.y-rect.center.y).Abs()<=(rect.height/2+height/2)))
                return true;

            return false;
        }

        public Fixed PointBorderDistance(Fixed2 pos)
        {
            if(Contains(pos))
            {
                return Fixed.Zero;
            }
            Fixed xDistance = Fixed.Zero;
            Fixed yDistance = Fixed.Zero;
            if (pos.x < x)
            {
                xDistance = x - pos.x;
            }
            else if (pos.x > (x + width))
            {
                xDistance = pos.x - (x + width);
            }
            

            if (pos.y < y)
            {
                yDistance = y - pos.y;
            }
            else if (pos.y > (y + height))
            {
                yDistance = pos.y - (y + height);
            }

            Fixed result = (Fixed.Pow(xDistance, 2.ToFixed()) + Fixed.Pow(yDistance, 2.ToFixed()));
            return result;
        }
    }

}