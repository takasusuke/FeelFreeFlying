using System;
using System.IO;
using System.Linq;
using FeelFreeFlying.Benchmark;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// ストリーミングしながらフレームレートを測るビルド（docs/m2-plan.md §5）。
    ///
    /// **M0と同じ軌道・同じ速度で測る。** 街を静的に置いた時の数字
    /// （2km四方で avg 368.7 / 1% low 106.3 → `m0-plan.md` §5.1）と並べられなければ、
    /// ストリーミングが割に合っているかを判断できない。
    ///
    /// 半径を広げると、タイルが読み込み・破棄の距離をまたぐので**出し入れの引っかかり**が測れる。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2StreamingBenchmarkBuild.BuildWindows64 `
    ///     -ffm2bench-radius 2200
    /// </summary>
    public static class M2StreamingBenchmarkBuild
    {
        private const string BenchScene = "Assets/Scenes/M2Bench.unity";
        private const string OutputDir = "Build/M2Bench";
        private const string ExecutableName = "FeelFreeFlying-M2Bench.exe";

        private const string RadiusArg = "-ffm2bench-radius";
        private const string HeightArg = "-ffm2bench-height";

        [MenuItem("Tools/FeelFreeFlying/M2: ストリーミングを計測するビルド (Windows64)")]
        public static void BuildWindows64()
        {
            float radius = 800f;
            float height = 300f;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == RadiusArg) float.TryParse(args[i + 1], out radius);
                if (args[i] == HeightArg) float.TryParse(args[i + 1], out height);
            }

            string[] tileScenes = Directory.Exists(M2TilePipeline.TileDir)
                ? Directory.GetFiles(M2TilePipeline.TileDir, "Tile_*.unity")
                    .Select(path => path.Replace('\\', '/'))
                    .OrderBy(path => path)
                    .ToArray()
                : new string[0];

            if (tileScenes.Length == 0)
            {
                Debug.LogError("[M2Bench] タイルがありません。先に M2: 街をタイルに割って取り込む を実行してください。");
                EditorApplication.Exit(1);
                return;
            }

            string[] farScenes = Directory.Exists(M2FarTiles.FarDir)
                ? Directory.GetFiles(M2FarTiles.FarDir, "Far_*.unity")
                    .Select(path => path.Replace('\\', '/'))
                    .OrderBy(path => path)
                    .ToArray()
                : new string[0];

            CreateScene(radius, height);
            M2TilePipeline.RegisterTilesInBuildSettings(tileScenes.Concat(farScenes));

            string[] scenes = new[] { BenchScene }.Concat(tileScenes).Concat(farScenes).ToArray();
            if (!PlayerBuildUtil.BuildWindows64(scenes, OutputDir, ExecutableName))
            {
                EditorApplication.Exit(1);
            }
        }

        private static void CreateScene(float radius, float height)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var pathObject = new GameObject("BenchmarkPath");
            FlightBenchmarkPath path = pathObject.AddComponent<FlightBenchmarkPath>();

            var pathSerialized = new SerializedObject(path);
            pathSerialized.FindProperty("generateHeight").floatValue = height;
            pathSerialized.FindProperty("generateRadius").floatValue = radius;
            pathSerialized.ApplyModifiedPropertiesWithoutUndo();
            path.GenerateCircularPath();

            Camera camera = Camera.main;
            if (camera == null)
            {
                var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
                camera = cameraObject.AddComponent<Camera>();
            }

            camera.nearClipPlane = 1f;
            camera.farClipPlane = 20000f;

            FlightBenchmarkRunner runner = camera.gameObject.AddComponent<FlightBenchmarkRunner>();
            var runnerSerialized = new SerializedObject(runner);
            runnerSerialized.FindProperty("path").objectReferenceValue = path;
            runnerSerialized.FindProperty("label").stringValue =
                $"ストリーミング_半径{radius:F0}_高度{height:F0}";
            runnerSerialized.FindProperty("quitOnFinish").boolValue = true;
            runnerSerialized.FindProperty("showHud").boolValue = false;
            runnerSerialized.ApplyModifiedPropertiesWithoutUndo();

            // **カメラを追いかけさせる。** 計測はカメラのTransformを動かすので、
            // タイルの読み込みもそこを基準にしないと測っている場所と噛み合わない
            var streamerObject = new GameObject("TileStreamer");
            TileStreamer streamer = streamerObject.AddComponent<TileStreamer>();

            var streamerSerialized = new SerializedObject(streamer);
            streamerSerialized.FindProperty("viewer").objectReferenceValue = camera.transform;
            streamerSerialized.FindProperty("showStatus").boolValue = false;
            streamerSerialized.ApplyModifiedPropertiesWithoutUndo();

            // 条件を実行時に外せるようにする（当たり判定・外壁）。**ビルドし直さずに切り分ける**
            streamerObject.AddComponent<TileBenchOptions>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, BenchScene);

            Debug.Log($"[M2Bench] 作成: {BenchScene} / 半径 {radius:F0} m・高度 {height:F0} m / " +
                      $"全長 {path.TotalLength:F0} m");
        }
    }
}
