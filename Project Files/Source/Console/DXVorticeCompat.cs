using System;
using System.Drawing;
using System.Numerics;
// Third-party: SharpDX compatibility shim over Vortice.Windows (MIT License, Copyright (c) Amer Koleci and
// Contributors). Full license text ships with the app (Licenses folder) and lives in the repo under
// Project Files\lib\licenses\.
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Thetis
{
    internal struct DXRectF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;

        public DXRectF(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public float Left
        {
            get { return X; }
            set { Width = Right - value; X = value; }
        }

        public float Top
        {
            get { return Y; }
            set { Height = Bottom - value; Y = value; }
        }

        public float Right
        {
            get { return X + Width; }
            set { Width = value - X; }
        }

        public float Bottom
        {
            get { return Y + Height; }
            set { Height = value - Y; }
        }

        public System.Drawing.SizeF Size
        {
            get { return new System.Drawing.SizeF(Width, Height); }
        }

        public void Inflate(float dx, float dy)
        {
            X -= dx;
            Y -= dy;
            Width += dx * 2f;
            Height += dy * 2f;
        }

        public void Offset(float dx, float dy)
        {
            X += dx;
            Y += dy;
        }

        public bool Contains(float x, float y)
        {
            return y >= Top && y <= Bottom && x >= Left && x <= Right;
        }

        public bool Contains(Point point)
        {
            return Contains(point.X, point.Y);
        }

        public bool Contains(PointF point)
        {
            return Contains(point.X, point.Y);
        }

        public bool Contains(Vector2 point)
        {
            return Contains(point.X, point.Y);
        }

        public static implicit operator RawRectF(DXRectF rect)
        {
            return new RawRectF(rect.X, rect.Y, rect.Right, rect.Bottom);
        }

        public override string ToString()
        {
            return string.Format("X:{0} Y:{1} Width:{2} Height:{3}", X, Y, Width, Height);
        }
    }

    internal static class DXVorticeExtensions
    {
        public static void DrawText(this ID2D1RenderTarget renderTarget, string text, IDWriteTextFormat textFormat, DXRectF layoutRect, ID2D1Brush brush)
        {
            renderTarget.DrawText(text, textFormat, new Rect(layoutRect.X, layoutRect.Y, layoutRect.Right, layoutRect.Bottom), brush);
        }

        public static void DrawText(this ID2D1RenderTarget renderTarget, string text, IDWriteTextFormat textFormat, DXRectF layoutRect, ID2D1Brush brush, DrawTextOptions options)
        {
            renderTarget.DrawText(text, textFormat, new Rect(layoutRect.X, layoutRect.Y, layoutRect.Right, layoutRect.Bottom), brush, options);
        }

        public static void DrawBitmap(this ID2D1RenderTarget renderTarget, ID2D1Bitmap bitmap, DXRectF destinationRectangle, float opacity, BitmapInterpolationMode interpolationMode)
        {
            renderTarget.DrawBitmap(bitmap, (RawRectF)destinationRectangle, opacity, interpolationMode, null);
        }
    }
}
