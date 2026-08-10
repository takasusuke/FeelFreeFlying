using System.Collections.Generic;
using System.IO;
using System.Linq;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// タイルを出し入れしながら飛ぶビルド（docs/m2-plan.md §4）。
    ///
    /// **M1の試遊シーンから街だけを抜く。** 操作・カメラ・HUD・起動画面はM1で決着がついており、
    /// ここで作り直すと比較にならない。街を静的に置く代わりに
    /// <see cref="TileStreamer"/> が近くのタイルだけを読む。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2StreamingBuild.BuildWindows64
    /// </summary>
    public static class M2StreamingBuild
    {
        private const string FlightScene = "Assets/Scenes/M1Flight.unity";
        private const string StreamingScene = "Assets/Scenes/M2Flight.unity";
        // **ビルドごとにフォルダを分ける。** 同じフォルダに複数のビルドを置くと、
        // ログに出るサイズが古いビルドを含んだ数字になる（1,595MBに見えて実際は半分だった）
        private const string OutputDir = "Build/M2Flight";
        private const string ExecutableName = "FeelFreeFlying-M2Flight.exe";

        [MenuItem("Tools/FeelFreeFlying/M2: ストリーミングで飛ぶビルド (Windows64)")]
        public static void BuildWindows64()
        {
            if (!CreateStreamingScene()) { EditorApplication.Exit(1); return; }

            string[] tileScenes = Directory.Exists(M2TilePipeline.TileDir)
                ? Directory.GetFiles(M2TilePipeline.TileDir, "Tile_*.unity")
                    .Select(path => path.Replace('\\', '/'))
                    .OrderBy(path => path)
                    .ToArray()
                : new string[0];

            if (tileScenes.Length == 0)
            {
                Debug.LogError("[M2Stream] タイルがありません。先に M2: 街をタイルに割って取り込む を実行してください。");
                EditorApplication.Exit(1);
                return;
            }

            // 遠景タイルも入れる。**入れないと実行時に読めず、街が途切れたまま**（→ m2-plan.md §4.6）
            string[] farScenes = Directory.Exists(M2FarTiles.FarDir)
                ? Directory.GetFiles(M2FarTiles.FarDir, "Far_*.unity")
                    .Select(path => path.Replace('\\', '/'))
                    .OrderBy(path => path)
                    .ToArray()
                : new string[0];

            M2TilePipeline.RegisterTilesInBuildSettings(tileScenes.Concat(farScenes));

            string[] scenes = new[] { StreamingScene }.Concat(tileScenes).Concat(farScenes).ToArray();
            if (!PlayerBuildUtil.BuildWindows64(scenes, OutputDir, ExecutableName))
            {
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("Tools/FeelFreeFlying/M2: ストリーミングのシーンを作る")]
        public static bool CreateStreamingScene()
        {
            // **毎回M1のシーンから作り直す。** 操作の調整はM1側にしか入らないので、
            // ここで古いシーンを使い回すと「直したはずの値が効かない」が再発する
            M1SceneSetup.CreateFlightScene();

            Scene scene = EditorSceneManager.OpenScene(FlightScene, OpenSceneMode.Single);
            int removed = RemoveStaticCity(scene);

            FlightController controller = Object.FindAnyObjectByType<FlightController>();
            if (controller == null)
            {
                Debug.LogError("[M2Stream] Flyerが見つかりません。M1のシーン生成が壊れています。");
                return false;
            }

            var streamerObject = new GameObject("TileStreamer");
            TileStreamer streamer = streamerObject.AddComponent<TileStreamer>();

            var serialized = new SerializedObject(streamer);
            serialized.FindProperty("viewer").objectReferenceValue = controller.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // 見どころの光の柱（→ docs/m3-plan.md §4.1）。
            // **計測ビルド（M2Bench）には載せない**——M0からの数字と並べられなくなる
            M3SpotSetup.AddBeacons(controller.transform);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, StreamingScene);

            // **街を消したぶんの容量は、別名で保存すれば一緒に落ちる。** シーンに埋め込まれた
            // メッシュ・テクスチャを明示的に捨てる処理も書いてみたが、捨てる対象は0個で、
            // 保存後のシーンは同じ0.1MBだった（M1で消し忘れが問題になったのは、
            // 参照だけ切って**同じシーンに上書き保存**した場合 → `m1-plan.md` §6.3）
            Debug.Log($"[M2Stream] 作成: {StreamingScene} / 静的に置かれていた街 {removed} 個を外した");
            return true;
        }

        /// <summary>
        /// 街を外す。**残すものを名指しで決める**——PLATEAUが作る階層は
        /// 都市によって名前が変わるので、消す側を名前で選ぶと取りこぼす。
        /// </summary>
        private static int RemoveStaticCity(Scene scene)
        {
            var keep = new List<GameObject>();

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                bool isRig =
                    root.GetComponentInChildren<FlightController>(true) != null ||
                    root.GetComponentInChildren<Camera>(true) != null ||
                    root.GetComponentInChildren<Light>(true) != null ||
                    root.GetComponentInChildren<FlightHud>(true) != null ||
                    root.GetComponentInChildren<TitleScreen>(true) != null ||
                    root.GetComponentInChildren<SettingsScreen>(true) != null ||
                    root.name == "Ocean";

                if (isRig) keep.Add(root);
            }

            int removed = 0;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (keep.Contains(root)) continue;
                Object.DestroyImmediate(root);
                removed++;
            }

            return removed;
        }
    }
}
