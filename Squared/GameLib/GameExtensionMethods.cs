﻿using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Squared.Game {
    public class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class {

        public bool Equals (T x, T y) {
            return (x == y);
        }

        public int GetHashCode (T obj) {
            return obj.GetHashCode();
        }
    }

    public static class GameExtensionMethods {
        public static double NextDouble (this Random random, double min, double max) {
            return (random.NextDouble() * (max - min)) + min;
        }

        public static float NextFloat (this Random random, float min, float max) {
            return ((float)random.NextDouble() * (max - min)) + min;
        }

        public static Vector2 Perpendicular (this Vector2 vector) {
            return new Vector2(-vector.Y, vector.X);
        }

        public static Vector2 PerpendicularLeft (this Vector2 vector) {
            return new Vector2(vector.Y, -vector.X);
        }

        public static Vector2 Round (this Vector2 vector) {
            return new Vector2((float)Math.Round(vector.X), (float)Math.Round(vector.Y));
        }

        public static Vector2 Round (this Vector2 vector, int decimals) {
            return new Vector2((float)Math.Round(vector.X, decimals), (float)Math.Round(vector.Y, decimals));
        }

        public static Vector2 Rotate (this Vector2 vector, float radians) {
            var cos = (float)Math.Cos(radians);
            var sin = (float)Math.Sin(radians);
            return new Vector2(
                (cos * vector.X - sin * vector.Y),
                (sin * vector.X + cos * vector.Y)
            );
        }

        public static float Dot (this Vector2 @this, ref Vector2 rhs) {
            float result;
            Vector2.Dot(ref @this, ref rhs, out result);
            return result;
        }

        public static Bounds BoundsFromRectangle (this Texture2D @this, ref Rectangle rectangle) {
            float fw = @this.Width;
            float fh = @this.Height;
            float xScale = 1f / fw, yScale = 1f / fh;
            var tl = new Vector2(rectangle.Left * xScale, rectangle.Top * yScale);
            var br = new Vector2(tl.X + (rectangle.Width * xScale), tl.Y + (rectangle.Height * yScale));
            return new Bounds(tl, br);
        }

        public static Bounds BoundsFromRectangle (this Texture2D @this, Rectangle rectangle) {
            return @this.BoundsFromRectangle(ref rectangle);
        }
    }
}
