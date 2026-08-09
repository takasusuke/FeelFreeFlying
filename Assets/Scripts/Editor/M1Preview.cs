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

        /// <summary>
        /// 屋上に降りた時の絵を出す。**当たり判定が実際に効いているかの確認も兼ねる**
        /// （レイが何にも当たらなければ、着地はゲーム中でも失敗する）。
        /// </summary>
        [MenuItem("Tools/FeelFreeFlying/M1: 屋上に降りた絵を書き出す")]
        public static void CaptureRooftop()
        {
            EditorSceneManager.OpenScene(FlightScene, OpenSceneMode.Single);

            var flyer = Object.FindFirstObjectByType<FlightController>();
            var camera = Object.FindFirstObjectByType<FlightCamera>();
            if (flyer == null || camera == null)
            {
                Debug.LogError("[M1Preview] 機体かカメラが見つかりません。");
                EditorApplication.Exit(1);
                return;
            }

            Bounds city = CalculateCityBounds();
            var origin = new Vector3(city.center.x, city.max.y + 50f, city.center.z);

            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 2000f))
            {
                Debug.LogError("[M1Preview] 真下に当たり判定がありません。**着地は機能しません。**");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[M1Preview] 着地点: {hit.collider.name} 高度 {hit.point.y:F1} m");

            flyer.transform.SetPositionAndRotation(
                hit.point + Vector3.up * 0.9f, Quaternion.Euler(4f, 25f, 0f));

            RenderToFile(camera, ResolveOutputPath());
        }

        /// <summary>
        /// 街の何点かで真下を調べ、**どこに降りられるか**を一覧で出す。
        /// 「屋上には降りられるが地面には降りられない」といった取りこぼしは、
        /// 飛んで試すより一覧で見たほうが早く分かる。
        /// </summary>
        [MenuItem("Tools/FeelFreeFlying/M1: 着地できる場所を調べる")]
        public static void ProbeLandingSpots()
        {
            EditorSceneManager.OpenScene(FlightScene, OpenSceneMode.Single);
            Bounds city = CalculateCityBounds();

            foreach (MeshCollider collider in Object.FindObjectsByType<MeshCollider>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Bounds b = collider.bounds;
                Debug.Log($"[M1Preview] コライダー: {collider.name} " +
                          $"（{b.size.x:F0} x {b.size.z:F0} m / 高さ {b.min.y:F0}〜{b.max.y:F0} m）");
            }

            var offsets = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0.25f, 0.25f),
                new Vector2(-0.25f, 0.25f),
                new Vector2(0.25f, -0.25f),
                new Vector2(-0.25f, -0.25f),
                new Vector2(0.45f, 0f),
                new Vector2(0f, -0.45f),
            };

            foreach (Vector2 offset in offsets)
            {
                var origin = new Vector3(
                    city.center.x + city.size.x * offset.x,
                    city.max.y + 50f,
                    city.center.z + city.size.z * offset.y);

                string result = Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 2000f)
                    ? $"{hit.collider.name} 高度 {hit.point.y:F1} m"
                    : "**当たり判定なし（着地できない）**";

                Debug.Log($"[M1Preview] ({origin.x:F0}, {origin.z:F0}) → {result}");
            }
        }

        /// <summary>
        /// 飛ぶ姿勢と歩く姿勢を、三人称で1枚ずつ書き出す。
        /// **自分の姿は一人称では消しているので、確認は三人称でしかできない。**
        /// マントが身体にめり込んでいないか等は、数字ではなく絵でしか分からない。
        /// </summary>
        [MenuItem("Tools/FeelFreeFlying/M1: 飛ぶ姿勢と歩く姿勢を書き出す")]
        public static void CapturePoses()
        {
            EditorSceneManager.OpenScene(FlightScene, OpenSceneMode.Single);

            var flyer = Object.FindFirstObjectByType<FlightController>();
            var camera = Object.FindFirstObjectByType<FlightCamera>();
            if (flyer == null || camera == null)
            {
                Debug.LogError("[M1Preview] 機体かカメラが見つかりません。");
                EditorApplication.Exit(1);
                return;
            }

            // 一人称のままだと身体を消してしまうので、三人称に切り替えて撮る
            var serialized = new SerializedObject(camera);
            serialized.FindProperty("mode").enumValueIndex = 0; // ThirdPerson
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Transform flying = flyer.transform.Find("PoseFly");
            Transform walking = flyer.transform.Find("PoseWalk");

            Bounds city = CalculateCityBounds();
            flyer.transform.SetPositionAndRotation(
                new Vector3(city.center.x, PreviewAltitude, city.center.z - PreviewDistance),
                Quaternion.Euler(-4f, 0f, 10f));

            string basePath = ResolveOutputPath();
            string directory = Path.GetDirectoryName(Path.GetFullPath(basePath));
            string name = Path.GetFileNameWithoutExtension(basePath);

            if (flying != null) flying.gameObject.SetActive(true);
            if (walking != null) walking.gameObject.SetActive(false);
            RenderToFile(camera, Path.Combine(directory, name + "-fly.png"));

            if (flying != null) flying.gameObject.SetActive(false);
            if (walking != null)
            {
                walking.gameObject.SetActive(true);
                walking.localRotation = Quaternion.identity;
            }
            RenderToFile(camera, Path.Combine(directory, name + "-walk.png"));
        }

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

            RenderToFile(camera, ResolveOutputPath());
        }

        private static void RenderToFile(FlightCamera camera, string path)
        {
            var cameraComponent = camera.GetComponent<Camera>();
            camera.SnapToTarget();

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
