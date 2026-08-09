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
    /// 遠景タイルを作る（docs/m2-plan.md §4.6）。
    ///
    /// **近景だけでは街が片側1〜1.5kmで途切れる。** 読み込み距離を広げるとfpsが落ちる（→ §6）ので、
    /// 遠くには**同じ街の軽いタイル**を置く。地域単位・LOD1・建物のテクスチャ無しで、
    /// 描画呼び出しが数千から数個に潰れる。
    ///
    ///   Unity.exe -projectPath . -batchmode -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2FarTiles.Build `
    ///     -ffm2tiles-grid &lt;メッシュコード&gt;
    /// </summary>
    public static class M2FarTiles
    {
        public const string FarDir = "Assets/Scenes/TilesFar";

        public static string FarScenePath(string gridCode) => $"{FarDir}/Far_{gridCode}.unity";

        [MenuItem("Tools/FeelFreeFlying/M2: 遠景タイルを作る")]
        public static void BuildFromMenu() => _ = BuildAsync(exitWhenDone: false);

        public static void Build() => _ = BuildAsync(exitWhenDone: true);

        private static async Task BuildAsync(bool exitWhenDone)
        {
            try
            {
                Directory.CreateDirectory(FarDir);

                string[] codes = M2TilePipeline.GridCodesInUse;
                var counts = new Dictionary<string, int>();

                foreach (string gridCode in codes)
                {
                    string scenePath = FarScenePath(gridCode);
                    Debug.Log($"[M2Far] {gridCode} を遠景として取り込みます → {scenePath}");

                    counts[gridCode] = await M0CityImport.ImportFarTile(gridCode, scenePath, codes);
                }

                RegisterInBuildSettings(codes.Select(FarScenePath));

                foreach (KeyValuePair<string, int> pair in counts)
                {
                    long bytes = File.Exists(FarScenePath(pair.Key))
                        ? new FileInfo(FarScenePath(pair.Key)).Length
                        : 0L;

                    Debug.Log($"[M2Far] {pair.Key}: 描画対象 {pair.Value} 個 / {bytes / 1024f / 1024f:F1} MB");
                }

                // **位置表に遠景の名前を足す。** 近景と同じ表に入れないと、
                // 実行時に「このタイルの遠景はどれか」が引けない
                M2TilePipeline.WriteFarNamesIntoCatalog(codes);

                Debug.Log($"[M2Far] 完了: {counts.Count} タイル");

                if (exitWhenDone) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M2Far] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        private static void RegisterInBuildSettings(IEnumerable<string> scenePaths)
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
