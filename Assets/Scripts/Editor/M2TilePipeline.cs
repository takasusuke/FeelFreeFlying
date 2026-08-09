using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// 街をタイルに割って変換する（docs/m2-plan.md §2）。**1タイル = 3次メッシュ1枚。**
    ///
    /// 全部を1つのシーンに置く方式は3km四方で1% lowが60fpsを割り、
    /// 取り込み中のEditorも12.3GBまで膨らむ（→ `m0-plan.md` §5.1）。
    /// 都市を増やすには「近くだけ読む」しかなく、そのためにまず**タイル単位のシーン**を作る。
    ///
    ///   Unity.exe -projectPath . -batchmode -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2TilePipeline.BuildTiles
    /// </summary>
    public static class M2TilePipeline
    {
        public const string TileDir = "Assets/Scenes/Tiles";

        /// <summary>新宿駅周辺。**M0の計測と同じ範囲**なので、数字を並べて比べられる。</summary>
        private static readonly string[] GridCodes =
        {
            "53394525", "53394526",
            "53394535", "53394536",
        };

        public static string TileScenePath(string gridCode) => $"{TileDir}/Tile_{gridCode}.unity";

        [MenuItem("Tools/FeelFreeFlying/M2: 街をタイルに割って取り込む")]
        public static void BuildTilesFromMenu() => _ = BuildTilesAsync(exitWhenDone: false);

        public static void BuildTiles() => _ = BuildTilesAsync(exitWhenDone: true);

        private static async Task BuildTilesAsync(bool exitWhenDone)
        {
            try
            {
                Directory.CreateDirectory(TileDir);
                var counts = new Dictionary<string, int>();

                foreach (string gridCode in GridCodes)
                {
                    string scenePath = TileScenePath(gridCode);
                    Debug.Log($"[M2Tile] {gridCode} を取り込みます → {scenePath}");

                    int buildings = await M0CityImport.ImportTile(gridCode, scenePath);
                    counts[gridCode] = buildings;
                }

                RegisterTilesInBuildSettings(GridCodes.Select(TileScenePath));

                foreach (KeyValuePair<string, int> pair in counts)
                {
                    long bytes = new FileInfo(TileScenePath(pair.Key)).Length;
                    Debug.Log($"[M2Tile] {pair.Key}: 建物 {pair.Value} 棟 / {bytes / 1024f / 1024f:F1} MB");
                }

                Debug.Log($"[M2Tile] 完了: {counts.Count} タイル / 合計 {counts.Values.Sum()} 棟");

                if (exitWhenDone) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M2Tile] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// タイルのシーンをBuild Settingsに登録する。
        /// **登録していないシーンは<see cref="UnityEngine.SceneManagement.SceneManager"/>から読めない。**
        /// </summary>
        public static void RegisterTilesInBuildSettings(IEnumerable<string> scenePaths)
        {
            var scenes = EditorBuildSettings.scenes.ToList();

            foreach (string path in scenePaths)
            {
                if (scenes.Any(entry => entry.path == path)) continue;
                scenes.Add(new EditorBuildSettingsScene(path, true));
            }

            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
