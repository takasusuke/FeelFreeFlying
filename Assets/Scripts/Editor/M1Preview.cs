using System.IO;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// 試遊シーンの見た目を1枚の画像に書き出す（docs/m1-plan.md）。
    ///
    /// **画面全体のスクリーンショットで確認しない。** 無関係なウィンドウまで写り込むうえ、
    /// ゲームウィンドウが前面に無いと撮れない。シーンのカメラから直接レンダリングすれば
    /// バッチモードで完結する。
    ///
    /// HUDはIMGUI（OnGUI）なのでこの画像には写らない。**確認できるのは3D側だけ**
    /// （機体が見えているか、海が出ているか、街との距離感）。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile <ログ> `
    ///     -executeMethod FeelFreeFlying.EditorTools.M1Preview.Capture -ffpreview-out <出力先.png>
    /// </summary>
    public static class M1Preview
    {
        private const string FlightScene = "Assets/Scenes/M1Flight.unity";
        private const string OutputArg = "-ffpreview-out";
        private const string DefaultOutput = "Temp/m1-preview.png";

        private const int Width = 1280;
        private const int Height = 720;

        /// <summary>街の中心からどれだけ手前で撮るか。街が正面に来る位置。</summary>
        private const float PreviewDistance = 420f;

        private const float PreviewAltitude = 165f;

        [MenuItem("Tools/FeelFreeFlying/M1: 見た目を画像に書き出す")]
        public static void Capture()
        {
            EditorSceneManager.OpenScene(FlightScene, OpenSceneMode.Single);

            var flyer = Object.FindFirstObjectByType<FlightController>();
            var camera = Object.FindFirstObjectByType<FlightCamera>();
            if (flyer == null || camera == null)
            {
                Debug.LogError("[M1Preview] 機体かカメラが見つかりません。先に試遊シーンを作ってください。");
                EditorApplication.Exit(1);
                return;
            }

            // 街の中心を出す。海（巨大な板）は数えない
            Bounds city = CalculateCityBounds();

            flyer.transform.SetPositionAndRotation(
                new Vector3(city.center.x, PreviewAltitude, city.center.z - PreviewDistance),
                Quaternion.Euler(-6f, 0f, 12f)); // 少し機首を上げ、傾けた姿勢のほうが姿が分かる

            var cameraComponent = camera.GetComponent<Camera>();
            camera.SnapToTarget();

            string path = ResolveOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));

            var renderTexture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
            };

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = cameraComponent.targetTexture;

            try
            {
                cameraComponent.targetTexture = renderTexture;
                cameraComponent.Render();

                RenderTexture.active = renderTexture;
                var image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                image.Apply();

                File.WriteAllBytes(path, image.EncodeToPNG());
                Object.DestroyImmediate(image);
            }
            finally
            {
                cameraComponent.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }

            Debug.Log($"[M1Preview] 出力: {Path.GetFullPath(path)}");
        }

        private static Bounds CalculateCityBounds()
        {
            Bounds? bounds = null;

            foreach (MeshRenderer renderer in Object.FindObjectsByType<MeshRenderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (renderer.name == "Ocean") continue;
                if (renderer.GetComponentInParent<FlightController>() != null) continue;

                bounds = bounds == null ? renderer.bounds : Encapsulated(bounds.Value, renderer.bounds);
            }

            return bounds ?? new Bounds(Vector3.zero, Vector3.one * 100f);
        }

        private static Bounds Encapsulated(Bounds a, Bounds b)
        {
            a.Encapsulate(b);
            return a;
        }

        private static string ResolveOutputPath()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == OutputArg) return args[i + 1];
            }
            return DefaultOutput;
        }
    }
}
