using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameFrame.Editor.Art
{
    internal enum ImageCropMode
    {
        Transparent,
        Auto,
        None
    }

    internal enum ImageResizeMode
    {
        None,
        Fit,
        LockWidth,
        LockHeight,
        RelativeScale
    }

    [Serializable]
    internal sealed class ImageCropSettings
    {
        public ImageCropMode CropMode = ImageCropMode.Auto;
        public int AlphaThreshold = 24;
        public int Padding = 2;
        public ImageResizeMode ResizeMode = ImageResizeMode.None;
        public int Width = 256;
        public int Height = 256;
        public float Scale = 1f;
    }

    internal static class ImageCropProcessor
    {
        internal static Texture2D LoadSource(string path)
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("源图只支持 PNG / JPG / JPEG。", nameof(path));
            }

            // 从磁盘解码，避免导入压缩、Max Size 和 Read/Write 设置影响实际输出。
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path)))
                {
                    throw new InvalidDataException($"无法解码图片：{path}");
                }

                return texture;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }

        internal static Vector2Int Export(string sourcePath, string outputPath, ImageCropSettings settings)
        {
            Texture2D source = null;
            Texture2D result = null;
            try
            {
                source = LoadSource(sourcePath);
                var crop = FindCropRect(source, settings);
                var size = GetOutputSize(new Vector2Int(source.width, source.height), crop, settings);
                result = Process(source, crop, size);
                File.WriteAllBytes(outputPath, result.EncodeToPNG());
                AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceUpdate);
                return size;
            }
            finally
            {
                if (result != null)
                {
                    UnityEngine.Object.DestroyImmediate(result);
                }

                if (source != null)
                {
                    UnityEngine.Object.DestroyImmediate(source);
                }
            }
        }

        internal static RectInt FindCropRect(Texture2D source, ImageCropSettings settings)
        {
            var width = source.width;
            var height = source.height;
            if (settings.CropMode == ImageCropMode.None)
            {
                return new RectInt(0, 0, width, height);
            }

            var pixels = source.GetPixels32();
            var hasTransparency = false;
            foreach (var pixel in pixels)
            {
                if (pixel.a < 250)
                {
                    hasTransparency = true;
                    break;
                }
            }

            var background = settings.CropMode == ImageCropMode.Auto && !hasTransparency
                ? FindEdgeBackground(pixels, width, height)
                : null;
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = y * width + x;
                    if (pixels[index].a <= settings.AlphaThreshold || (background != null && background[index]))
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < 0)
            {
                if (background != null)
                {
                    // 黑白图没有可区分的主体时保留全图，与参考工具一致。
                    return new RectInt(0, 0, width, height);
                }

                throw new InvalidOperationException("图片在当前裁剪规则下没有可见内容，请调整阈值或选择不裁剪。");
            }

            var padding = Math.Max(0, settings.Padding);
            minX = Math.Max(0, minX - padding);
            minY = Math.Max(0, minY - padding);
            maxX = Math.Min(width - 1, maxX + padding);
            maxY = Math.Min(height - 1, maxY + padding);
            return new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private static bool[] FindEdgeBackground(Color32[] pixels, int width, int height)
        {
            var background = new bool[pixels.Length];
            var queue = new Queue<int>();

            void Enqueue(int x, int y)
            {
                var index = y * width + x;
                var pixel = pixels[index];
                var min = Math.Min(pixel.r, Math.Min(pixel.g, pixel.b));
                var max = Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
                if (background[index] || !((min >= 185 || max <= 70) && max - min <= 40))
                {
                    return;
                }

                background[index] = true;
                queue.Enqueue(index);
            }

            for (var x = 0; x < width; x++)
            {
                Enqueue(x, 0);
                Enqueue(x, height - 1);
            }

            for (var y = 0; y < height; y++)
            {
                Enqueue(0, y);
                Enqueue(width - 1, y);
            }

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                        {
                            Enqueue(x + dx, y + dy);
                        }
                    }
                }
            }

            return background;
        }

        internal static Vector2Int GetOutputSize(Vector2Int sourceSize, RectInt crop, ImageCropSettings settings)
        {
            double scale;
            switch (settings.ResizeMode)
            {
                case ImageResizeMode.None:
                    return new Vector2Int(crop.width, crop.height);
                case ImageResizeMode.LockWidth:
                    scale = (double)settings.Width / crop.width;
                    break;
                case ImageResizeMode.LockHeight:
                    scale = (double)settings.Height / crop.height;
                    break;
                case ImageResizeMode.RelativeScale:
                    // 与参考工具一致：比例以原文件尺寸为目标框，再将裁剪结果等比装入。
                    scale = Math.Min(Math.Max(1, Math.Round(sourceSize.x * settings.Scale)) / crop.width,
                        Math.Max(1, Math.Round(sourceSize.y * settings.Scale)) / crop.height);
                    break;
                default:
                    scale = Math.Min((double)settings.Width / crop.width, (double)settings.Height / crop.height);
                    break;
            }

            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "缩放尺寸或比例必须大于 0。");
            }

            var width = Math.Max(1, Math.Round(crop.width * scale));
            var height = Math.Max(1, Math.Round(crop.height * scale));
            if (width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "输出尺寸超过当前设备的纹理大小上限。");
            }

            return new Vector2Int((int)width, (int)height);
        }

        internal static Texture2D Process(Texture2D source, RectInt crop, Vector2Int size)
        {
            var pixels = source.GetPixels(crop.x, crop.y, crop.width, crop.height);
            if (size.x != crop.width || size.y != crop.height)
            {
                // 使用预乘 Alpha 的浮点 Lanczos，透明像素中的 RGB 不污染可见边缘。
                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    pixels[i] = new Color(pixel.r * pixel.a, pixel.g * pixel.a, pixel.b * pixel.a, pixel.a);
                }

                pixels = Resample(pixels, crop.width, crop.height, size.x, true);
                pixels = Resample(pixels, size.x, crop.height, size.y, false);
                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    var alpha = Mathf.Clamp01(pixel.a);
                    pixels[i] = alpha > 0.000001f
                        ? new Color(Mathf.Clamp01(pixel.r / alpha), Mathf.Clamp01(pixel.g / alpha),
                            Mathf.Clamp01(pixel.b / alpha), alpha)
                        : Color.clear;
                }
            }

            var result = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            result.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                result.SetPixels(pixels);
                result.Apply(false, false);
                return result;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(result);
                throw;
            }
        }

        private static Color[] Resample(Color[] source, int width, int height, int targetLength, bool horizontal)
        {
            var sourceLength = horizontal ? width : height;
            if (sourceLength == targetLength)
            {
                return source;
            }

            var resultWidth = horizontal ? targetLength : width;
            var resultHeight = horizontal ? height : targetLength;
            var result = new Color[checked(resultWidth * resultHeight)];
            var filterScale = Math.Max(1d, (double)sourceLength / targetLength);
            for (var position = 0; position < targetLength; position++)
            {
                var center = (position + 0.5d) * sourceLength / targetLength - 0.5d;
                var start = Math.Max(0, (int)Math.Ceiling(center - 3 * filterScale));
                var end = Math.Min(sourceLength - 1, (int)Math.Floor(center + 3 * filterScale));
                var weights = new float[end - start + 1];
                var total = 0f;
                for (var sample = start; sample <= end; sample++)
                {
                    var weight = Lanczos((center - sample) / filterScale);
                    weights[sample - start] = weight;
                    total += weight;
                }

                var lines = horizontal ? height : width;
                for (var line = 0; line < lines; line++)
                {
                    var color = Color.clear;
                    for (var sample = start; sample <= end; sample++)
                    {
                        var index = horizontal ? line * width + sample : sample * width + line;
                        color += source[index] * (weights[sample - start] / total);
                    }

                    result[horizontal ? line * resultWidth + position : position * resultWidth + line] = color;
                }
            }

            return result;
        }

        private static float Lanczos(double distance)
        {
            distance = Math.Abs(distance);
            if (distance < 0.000001d)
            {
                return 1f;
            }

            if (distance >= 3d)
            {
                return 0f;
            }

            var radians = Math.PI * distance;
            return (float)(Math.Sin(radians) * Math.Sin(radians / 3d) / (radians * radians / 3d));
        }
    }
}
