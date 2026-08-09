using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// タイルごとにオクルージョンカリングを焼く（docs/m2-plan.md §2 の「6. 焼き込み」）。
    ///
    /// **街路の高さで最も効く。** M0では1% lowが60→360、最悪フレームが15→147msになった
    /// （→ `m0-plan.md` §5.1）。飛ぶだけなら上空しか通らないが、
    /// M1で屋上に降りて歩けるようにした以上、ビルの谷間の数字がそのまま体験に出る。
    ///
    /// **タイルは別々に焼く。** 隣のタイルの建物は遮蔽物として数えられないので、
    /// 1シーンで焼いた場合より控えめな結果になる。タイル境界で効きが落ちるのは
    /// 分割の代償であり、**どの程度かは測ってから判断する**。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2TileOcclusion.Bake
    /// </summary>
    public static class M2TileOcclusion
    {
        [MenuItem("Tools/FeelFreeFlying/M2: タイルにオクルージョンカリングを焼く")]
        public static void BakeFromMenu() => Run(exitWhenDone: false);

        public static void Bake() => Run(exitWhenDone: true);

        /// <summary>取り込みの続きとして呼ぶ入口。**ここでは終了しない。**</summary>
        public static void BakeAll() => Run(exitWhenDone: false);

        private static void Run(bool exitWhenDone)
        {
            try
            {
                string[] scenes = Directory.Exists(M2TilePipeline.TileDir)
                    ? Directory.GetFiles(M2TilePipeline.TileDir, "Tile_*.unity")
                        .Select(path => path.Replace('\\', '/'))
                        .OrderBy(path => path)
                        .ToArray()
                    : new string[0];

                if (scenes.Length == 0)
                {
                    Debug.LogError("[M2Occlusion] タイルがありません。");
                    if (exitWhenDone) EditorApplication.Exit(1);
                    return;
                }

                foreach (string scenePath in scenes)
                {
                    Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    int marked = MarkStatic();

                    // **シーンを開いた後に設定する。** これらはシーンに保存される値なので、
                    // 開く前に入れても開いた瞬間にシーン側の値で上書きされる
                    // （穴の大きさが1mのつもりで0.25mで焼き始めていた）。
                    // 建物の谷間を通れる大きさに合わせる（M0と同じ値。**条件を変えない**）
                    StaticOcclusionCulling.smallestOccluder = 5f;
                    StaticOcclusionCulling.smallestHole = 1f;
                    StaticOcclusionCulling.backfaceThreshold = 100f;

                    var stopwatch = Stopwatch.StartNew();
                    StaticOcclusionCulling.Compute();
                    stopwatch.Stop();

                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene, scenePath);

                    Debug.Log($"[M2Occlusion] {Path.GetFileNameWithoutExtension(scenePath)}: " +
                              $"{marked} 件 / {stopwatch.Elapsed.TotalMinutes:F1} 分 / " +
                              $"データ {DataSizeMegabytes(scenePath):F1} MB");
                }

                if (exitWhenDone) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M2Occlusion] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// 焼いたデータの大きさ。
        /// **<see cref="StaticOcclusionCulling.umbraDataSize"/>はバッチモードでは0を返す**ので、
        /// シーンの隣に書かれるアセットを直接見る。
        /// </summary>
        private static float DataSizeMegabytes(string scenePath)
        {
            string directory = Path.Combine(
                Path.GetDirectoryName(scenePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(scenePath));

            string asset = Path.Combine(directory, "OcclusionCullingData.asset");
            return File.Exists(asset) ? new FileInfo(asset).Length / 1024f / 1024f : 0f;
        }

        /// <summary>焼く対象はstaticな描画対象だけ。タイルの中身は全部動かないので全部付ける。</summary>
        private static int MarkStatic()
        {
            var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .ToList();

            foreach (MeshRenderer renderer in renderers)
            {
                GameObjectUtility.SetStaticEditorFlags(
                    renderer.gameObject,
                    StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic |
                    StaticEditorFlags.BatchingStatic);
            }

            return renderers.Count;
        }
    }
}
