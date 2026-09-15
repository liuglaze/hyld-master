using UnityEngine;

public static class BattleFloatMath
{
    private const float MoveEpsilon = 1e-6f;

    public static Vector3 ToWorldDirection(float towardX, float towardY, int sign)
    {
        Vector3 dir = new Vector3(towardY, 0f, towardX).normalized;
        dir.x *= -1f * sign;
        dir.z *= sign;
        return dir;
    }

    public static Vector3 ToMoveDirection(float moveX, float moveY, int sign)
    {
        Vector3 dir = new Vector3(-moveX * sign, 0f, moveY * sign);
        float len = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
        return len <= MoveEpsilon ? Vector3.zero : dir / len;
    }

    public static Vector3 AdvancePosition(Vector3 startPos, float moveX, float moveY, int sign, float speed, float frameTime)
    {
        Vector3 dir = ToMoveDirection(moveX, moveY, sign);
        if (dir == Vector3.zero)
        {
            return startPos;
        }
        return startPos + dir * speed * frameTime;
    }
}
