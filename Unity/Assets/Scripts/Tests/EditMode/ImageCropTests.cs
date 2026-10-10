using System;
using System.IO;
using GameFrame.Editor.Art;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GameFrame.Tests
{
    public sealed class ImageCropTests
    {
        private Texture2D source;
        private Texture2D result;
        private string assetFolder;

        [TearDown]
        public void TearDown()
        {
            if (result != null)
            {
                UnityEngine.Object.DestroyImmediate(result);
            }

            if (source != null)
            {
                UnityEngine.Object.DestroyImmediate(source);
            }

            if (assetFolder != null)
            {
                AssetDatabase.DeleteAsset(assetFolder);
                assetFolder = null;
            }
        }

        [Test]
        public void TransparentCropPreservesPixelCoordinatesAndPadding()
        {
            source = CreateImage(10, 8, Color.clear);
            source.SetPixel(3, 1, Color.red);
            source.SetPixel(5, 4, Color.blue);
            source.Apply();
            var settings = new ImageCropSettings { Padding = 0 };
            var crop = ImageCropProcessor.FindCropRect(source, settings);
            Assert.AreEqual(new RectInt(3, 1, 3, 4), crop);
            result = ImageCropProcessor.Process(source, crop, new Vector2Int(3, 4));
            Assert.AreEqual((Color32)Color.red, (Color32)result.GetPixel(0, 0));
            Assert.AreEqual((Color32)Color.blue, (Color32)result.GetPixel(2, 3));
            settings.Padding = 2;
            Assert.AreEqual(new RectInt(1, 0, 7, 7), ImageCropProcessor.FindCropRect(source, settings));
        }

        [Test]
        public void AlphaThresholdIgnoresFaintBorderAndRejectsEmptyContent()
        {
            source = CreateImage(4, 4, Color.clear);
            source.SetPixel(0, 0, new Color(1, 0, 0, 0.05f));
            source.SetPixel(2, 2, Color.red);
            source.Apply();
            var settings = new ImageCropSettings { Padding = 0 };
            Assert.AreEqual(new RectInt(2, 2, 1, 1), ImageCropProcessor.FindCropRect(source, settings));
            settings.AlphaThreshold = 0;
            Assert.AreEqual(new RectInt(0, 0, 3, 3), ImageCropProcessor.FindCropRect(source, settings));
            source.SetPixel(2, 2, Color.clear);
            source.Apply();
            settings.AlphaThreshold = 24;
            Assert.Throws<InvalidOperationException>(() => ImageCropProcessor.FindCropRect(source, settings));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AutoCropFindsColoredSubjectOnWhiteOrBlackBackground(bool white)
        {
            source = CreateImage(9, 7, white ? Color.white : Color.black);
            for (var y = 2; y < 5; y++)
            {
                for (var x = 2; x < 7; x++)
                {
                    source.SetPixel(x, y, Color.red);
                }
            }

            source.SetPixel(4, 3, Color.white);
            source.Apply();
            var settings = new ImageCropSettings { Padding = 0 };
            var crop = ImageCropProcessor.FindCropRect(source, settings);
            Assert.AreEqual(new RectInt(2, 2, 5, 3), crop);
            result = ImageCropProcessor.Process(source, crop, new Vector2Int(5, 3));
            Assert.AreEqual((Color32)Color.white, (Color32)result.GetPixel(2, 1), "框内白色像素必须保留");
            settings.CropMode = ImageCropMode.Transparent;
            Assert.AreEqual(new RectInt(0, 0, 9, 7), ImageCropProcessor.FindCropRect(source, settings));
        }

        [Test]
        public void AutoCropKeepsSolidWhiteImageAndNoCropKeepsTransparentImage()
        {
            source = CreateImage(4, 3, Color.white);
            var settings = new ImageCropSettings { Padding = 0 };
            Assert.AreEqual(new RectInt(0, 0, 4, 3), ImageCropProcessor.FindCropRect(source, settings));
            source.SetPixels(new Color[12]);
            source.Apply();
            settings.CropMode = ImageCropMode.None;
            Assert.AreEqual(new RectInt(0, 0, 4, 3), ImageCropProcessor.FindCropRect(source, settings));
        }

        [TestCase((int)ImageResizeMode.None, 100, 50)]
        [TestCase((int)ImageResizeMode.Fit, 40, 20)]
        [TestCase((int)ImageResizeMode.LockWidth, 40, 20)]
        [TestCase((int)ImageResizeMode.LockHeight, 80, 40)]
        [TestCase((int)ImageResizeMode.RelativeScale, 60, 30)]
        public void ResizeUsesCropAspectAndRelativeScaleUsesOriginalBox(int mode, int width, int height)
        {
            var settings = new ImageCropSettings { ResizeMode = (ImageResizeMode)mode, Width = 40, Height = 40, Scale = 0.5f };
            var size = ImageCropProcessor.GetOutputSize(new Vector2Int(200, 60), new RectInt(0, 0, 100, 50), settings);
            Assert.AreEqual(new Vector2Int(width, height), size);
        }

        [Test]
        public void PremultipliedResizePreventsTransparentRgbFromBleeding()
        {
            source = CreateImage(2, 1, new Color(0, 0, 1, 0));
            source.SetPixel(0, 0, Color.red);
            source.Apply();
            result = ImageCropProcessor.Process(source, new RectInt(0, 0, 2, 1), new Vector2Int(1, 1));
            var pixel = result.GetPixel(0, 0);
            Assert.That(pixel.a, Is.EqualTo(0.5f).Within(0.01f));
            Assert.That(pixel.r, Is.GreaterThan(0.99f));
            Assert.That(pixel.b, Is.LessThan(0.01f));
        }

        [Test]
        public void DownscaleRetainsContributionsFromAllSourcePixels()
        {
            source = CreateImage(8, 1, Color.black);
            source.SetPixel(0, 0, Color.white);
            source.SetPixel(7, 0, Color.white);
            source.Apply();
            result = ImageCropProcessor.Process(source, new RectInt(0, 0, 8, 1), new Vector2Int(1, 1));
            Assert.That(result.GetPixel(0, 0).r, Is.GreaterThan(0.1f), "不能只采样中心像素");
        }

        [Test]
        public void ExportReadsOriginalPixelsAndPreservesGuidAndImporterWhenOverwriting()
        {
            CreateAssetFolder();
            var path = assetFolder + "/original.png";
            source = CreateImage(64, 32, Color.clear);
            for (var y = 8; y < 24; y++)
            {
                for (var x = 16; x < 48; x++)
                {
                    source.SetPixel(x, y, Color.red);
                }
            }

            source.Apply();
            File.WriteAllBytes(path, source.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 32;
            importer.isReadable = false;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
            Assert.AreEqual(32, AssetDatabase.LoadAssetAtPath<Texture2D>(path).width);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var meta = File.ReadAllBytes(path + ".meta");
            var copy = assetFolder + "/copy.png";
            var settings = new ImageCropSettings { Padding = 0 };
            Assert.AreEqual(new Vector2Int(32, 16), ImageCropProcessor.Export(path, copy, settings));
            Assert.AreEqual(new Vector2Int(32, 16), ImageCropProcessor.Export(path, path, settings));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
            result = ImageCropProcessor.LoadSource(copy);
            Assert.AreEqual((Color32)Color.red, (Color32)result.GetPixel(0, 0));
            importer = (TextureImporter)AssetImporter.GetAtPath(path);
            Assert.AreEqual(32, importer.maxTextureSize);
            Assert.IsFalse(importer.isReadable);
            Assert.AreEqual(TextureImporterCompression.Compressed, importer.textureCompression);
        }

        [Test]
        public void BatchPreflightRejectsCollisionsPathTraversalAndUnintentionalSourceOverwrite()
        {
            CreateAssetFolder();
            source = CreateImage(2, 2, Color.red);
            var png = assetFolder + "/same.png";
            var jpg = assetFolder + "/same.jpg";
            File.WriteAllBytes(png, source.EncodeToPNG());
            File.WriteAllBytes(jpg, source.EncodeToJPG());
            AssetDatabase.ImportAsset(png);
            AssetDatabase.ImportAsset(jpg);
            Assert.Throws<ArgumentException>(() => ImageCropWindow.BuildOutputPaths(new[] { png, jpg }, assetFolder, null, false));
            Assert.Throws<ArgumentException>(() => ImageCropWindow.BuildOutputPaths(new[] { png }, assetFolder, "../escape.png", false));
            Assert.Throws<ArgumentException>(() => ImageCropWindow.BuildOutputPaths(new[] { png }, assetFolder, "same.png", false));
            Assert.Throws<ArgumentException>(() => ImageCropWindow.BuildOutputPaths(new[] { jpg }, null, null, true));
            Assert.Throws<ArgumentException>(() => ImageCropWindow.BuildOutputPaths(new[] { png }, "Packages", "copy.png", false));
            CollectionAssert.AreEqual(new[] { png }, ImageCropWindow.BuildOutputPaths(new[] { png }, null, null, true));
            CollectionAssert.AreEqual(new[] { assetFolder + "/copy.png" }, ImageCropWindow.BuildOutputPaths(new[] { jpg }, assetFolder, "copy", false));
        }

        private void CreateAssetFolder()
        {
            assetFolder = "Assets/__ImageCropTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(assetFolder));
        }

        private static Texture2D CreateImage(int width, int height, Color fill)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fill;
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }
}
