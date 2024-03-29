﻿using System;

namespace ImageCompression
{
    struct Point
    {
        public int x { get; set; }
        public int y { get; set; }

        public Point(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    class MoVector
    {
        public static float MAD(float[,] target, float[,] reference, int x, int y, int i, int j)
        {
            float result = 0;
            for (int k = 0; k < Constants.BLOCKS; ++k)
            {
                for (int h = 0; h < Constants.BLOCKS; ++h)
                {
                    if (x + k < 0 || x + k >= target.GetLength(0)) return float.MaxValue;
                    if (y + h < 0 || y + h >= target.GetLength(1)) return float.MaxValue;
                    if (x + i + k < 0 || x + i + k >= reference.GetLength(0)) return float.MaxValue;
                    if (y + j + h < 0 || y + j + h >= reference.GetLength(1)) return float.MaxValue;
                    result += MathF.Abs(target[x + k, y + h] - reference[x + i + k, y + j + h]);
                }
            }
            return result;
        }

        public static Point SeqSearch(float[,] target, float[,] reference, int x, int y)
        {
            float min = MAD(target, reference, x, y, 0, 0);
            if (min == 0) return new Point(0, 0);
            int u = 0;
            int v = 0;
            for (int i = -Constants.P; i < Constants.P; ++i)
            {
                for (int j = -Constants.P; j < Constants.P; ++j)
                {
                    float curr = MAD(target, reference, x, y, i, j);
                    if (curr < min)
                    {
                        min = curr;
                        u = i;
                        v = j;
                    }
                }
            }
            return new Point(u, v);
        }
    }
}
