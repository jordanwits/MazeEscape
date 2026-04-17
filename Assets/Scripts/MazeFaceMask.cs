using System;
using UnityEngine;

[Flags]
public enum MazeFaceMask
{
    None = 0,
    North = 1 << 0,
    East = 1 << 1,
    South = 1 << 2,
    West = 1 << 3
}

public enum MazePieceCategory
{
    DeadEnd,
    Straight,
    Corner,
    Tee,
    Cross,
    Special
}

public static class MazeFaceMaskUtility
{
    public static MazeFaceMask Rotate(MazeFaceMask faces, int quarterTurns)
    {
        int normalizedTurns = ((quarterTurns % 4) + 4) % 4;
        MazeFaceMask current = faces;

        for (int i = 0; i < normalizedTurns; i++)
        {
            MazeFaceMask rotated = MazeFaceMask.None;
            if ((current & MazeFaceMask.North) != 0)
                rotated |= MazeFaceMask.East;
            if ((current & MazeFaceMask.East) != 0)
                rotated |= MazeFaceMask.South;
            if ((current & MazeFaceMask.South) != 0)
                rotated |= MazeFaceMask.West;
            if ((current & MazeFaceMask.West) != 0)
                rotated |= MazeFaceMask.North;

            current = rotated;
        }

        return current;
    }

    public static bool IsStraight(MazeFaceMask faces)
    {
        return faces == (MazeFaceMask.North | MazeFaceMask.South)
            || faces == (MazeFaceMask.East | MazeFaceMask.West);
    }

    public static int CountOpenFaces(MazeFaceMask faces)
    {
        int count = 0;
        if ((faces & MazeFaceMask.North) != 0)
            count++;
        if ((faces & MazeFaceMask.East) != 0)
            count++;
        if ((faces & MazeFaceMask.South) != 0)
            count++;
        if ((faces & MazeFaceMask.West) != 0)
            count++;

        return count;
    }

    public static MazeFaceMask Opposite(MazeFaceMask direction)
    {
        return direction switch
        {
            MazeFaceMask.North => MazeFaceMask.South,
            MazeFaceMask.East => MazeFaceMask.West,
            MazeFaceMask.South => MazeFaceMask.North,
            MazeFaceMask.West => MazeFaceMask.East,
            _ => MazeFaceMask.None
        };
    }

    public static Vector3 ToVector3(MazeFaceMask direction)
    {
        return direction switch
        {
            MazeFaceMask.North => Vector3.forward,
            MazeFaceMask.East => Vector3.right,
            MazeFaceMask.South => Vector3.back,
            MazeFaceMask.West => Vector3.left,
            _ => Vector3.zero
        };
    }
}
